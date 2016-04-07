﻿//  Copyright (C) 2015, The Duplicati Team
//  http://www.duplicati.com, info@duplicati.com
//
//  This library is free software; you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as
//  published by the Free Software Foundation; either version 2.1 of the
//  License, or (at your option) any later version.
//
//  This library is distributed in the hope that it will be useful, but
//  WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
//  Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public
//  License along with this library; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA 02111-1307 USA
using System;
using CoCoL;
using System.Threading.Tasks;
using Duplicati.Library.Main.Database;
using Duplicati.Library.Main.Volumes;
using Duplicati.Library.Main.Operation.Common;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace Duplicati.Library.Main.Operation.Backup
{
    /// <summary>
    /// This class receives data blocks, registers then in the database.
    /// New blocks are added to a compressed archive and sent
    /// to the uploader
    /// </summary>
    internal static class DataBlockProcessor
    {
        public static int _ThreadId = 0;
        public static long _BlockCount = 0;
        public static long _ActiveBlockCount = 0;
        public static Dictionary<long, long> _Pos = new Dictionary<long, long>();

        public static Task Run(BackupDatabase database, Options options, ITaskReader taskreader)
        {
            return AutomationExtensions.RunTask(
            new
            {
                LogChannel = Common.Channels.LogChannel.ForWrite,
                Input = Channels.OutputBlocks.ForRead,
                Output = Channels.BackendRequest.ForWrite,
                SpillPickup = Channels.SpillPickup.ForWrite,
            },

            async self =>
            {
                BlockVolumeWriter blockvolume = null;
                var log = new LogWrapper(self.LogChannel);
                var tid = System.Threading.Interlocked.Increment(ref _ThreadId);

                Console.WriteLine("Started block processor {0}", tid);

                try
                {
                    while(true)
                    {
                        _Pos[tid] = 0;
                        var b = await self.Input.ReadAsync();
                        _Pos[tid] = 1;

                        System.Threading.Interlocked.Increment(ref _BlockCount);
                        System.Threading.Interlocked.Increment(ref _ActiveBlockCount);

                        // Lazy-start a new block volume
                        if (blockvolume == null)
                        {
                            // Before we start a new volume, probe to see if it exists
                            // This will delay creation of volumes for differential backups
                            // There can be a race, such that two workers determine that
                            // the block is missing, but this will be solved by the AddBlock call
                            // which runs atomically
                            _Pos[tid] = 2;
                            if (await database.FindBlockIDAsync(b.HashKey, b.Size) >= 0)
                            {
                                _Pos[tid] = 3;
                                b.TaskCompletion.TrySetResult(false);
                                System.Threading.Interlocked.Increment(ref _ActiveBlockCount);
                                continue;
                            }

                            _Pos[tid] = 4;
                            blockvolume = new BlockVolumeWriter(options);
                            blockvolume.VolumeID = await database.RegisterRemoteVolumeAsync(blockvolume.RemoteFilename, RemoteVolumeType.Blocks, RemoteVolumeState.Temporary);
                        }

                        _Pos[tid] = 5;
                        var newBlock = await database.AddBlockAsync(b.HashKey, b.Size, blockvolume.VolumeID);
                        b.TaskCompletion.TrySetResult(newBlock);

                        if (newBlock)
                        {
                            _Pos[tid] = 6;
                            blockvolume.AddBlock(b.HashKey, b.Data, b.Offset, (int)b.Size, b.Hint);

                            // If the volume is full, send to upload
                            if (blockvolume.Filesize > options.VolumeSize - options.Blocksize)
                            {
                                //When uploading a new volume, we register the volumes and then flush the transaction
                                // this ensures that the local database and remote storage are as closely related as possible
                                _Pos[tid] = 7;
                                await database.UpdateRemoteVolumeAsync(blockvolume.RemoteFilename, RemoteVolumeState.Uploading, -1, null);
                            
                                blockvolume.Close();

                                _Pos[tid] = 8;
                                await database.CommitTransactionAsync("CommitAddBlockToOutputFlush");

                                _Pos[tid] = 9;
                                await self.Output.WriteAsync(new VolumeUploadRequest(blockvolume, true));
                                blockvolume = null;
                            }

                        }

                        _Pos[tid] = 10;
                        System.Threading.Interlocked.Decrement(ref _ActiveBlockCount);

                        // We ignore the stop signal, but not the pause and terminate
                        await taskreader.ProgressAsync;
                    }
                }
                catch(Exception ex)
                {
                    if (ex.IsRetiredException())
                    {
                        // If we have collected data, merge all pending volumes into a single volume
                        if (blockvolume != null && blockvolume.SourceSize > 0)
                        {
                            Console.WriteLine("Spilling from block processor {0}", tid);

                            await self.SpillPickup.WriteAsync(new VolumeUploadRequest(blockvolume, true));
                        }

                        Console.WriteLine("Finished block processor {0}", tid);

                    }
                    else
                        Console.WriteLine("Crashed block processor {0} - {1}", tid, ex);

                    throw;
                }
                finally
                {
                    System.Threading.Interlocked.Decrement(ref _ThreadId);
                    Console.WriteLine("Quit block processor {0}, active: {1}", tid, _ThreadId);
                }
            });
        }



    }
}

