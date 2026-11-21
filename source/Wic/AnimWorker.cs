/*
ImageGlass WIC Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
https://github.com/d2phap/wic-imageglass-plugin
*/
using ImageGlass.SDK.Plugins;

namespace WicCodec.Wic;

/// <summary>
/// The one MTA thread that owns every <see cref="AnimPlayer"/>, since a COM object must stay in the apartment that made it.
/// </summary>
internal static unsafe class AnimWorker
{
    private static readonly Lock _lock = new();
    private static Worker? _current;


    /// <summary>
    /// Renders frame <paramref name="index"/> on the worker thread, blocking the caller until it is done.
    /// </summary>
    public static IGStatus Render(string path, AnimTimeline timeline, int index, IGPixelBuffer* outBuf, void* cancellation)
    {
        Worker worker;
        lock (_lock)
        {
            worker = _current ??= new Worker();
        }
        return worker.Run(path, timeline, index, outBuf, cancellation);
    }


    /// <summary>Releases every player and ends the thread; the next render starts a new one.</summary>
    public static void Stop()
    {
        Worker? worker;
        lock (_lock)
        {
            worker = _current;
            _current = null;
        }
        worker?.Stop();
    }


    private sealed class Job
    {
        public string Path = "";
        public AnimTimeline? Timeline;
        public int Index;
        public nint OutBuf;
        public nint Cancellation;
        public bool IsStop;
        public IGStatus Result = IGStatus.Internal;
        public readonly ManualResetEventSlim Done = new(false);
    }


    private sealed class Worker
    {
        // Each player holds the file, a decoder and for GIF two canvases, so only the latest few stay.
        private const int MAX_PLAYERS = 2;

        // A paused or abandoned animation should not pin its memory forever.
        private static readonly TimeSpan _idleRelease = TimeSpan.FromSeconds(20);

        private readonly object _gate = new();
        private readonly Queue<Job> _queue = new();
        private readonly Thread _thread;
        private bool _stopped;

        // Worker thread only; most recently used last.
        private readonly List<AnimPlayer> _players = [];


        public Worker()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "WicCodec animation" };
            _thread.Start();
        }


        public IGStatus Run(string path, AnimTimeline timeline, int index, IGPixelBuffer* outBuf, void* cancellation)
        {
            var job = new Job
            {
                Path = path,
                Timeline = timeline,
                Index = index,
                OutBuf = (nint)outBuf,
                Cancellation = (nint)cancellation,
            };

            lock (_gate)
            {
                if (_stopped) return IGStatus.Canceled;
                _queue.Enqueue(job);
                Monitor.Pulse(_gate);
            }

            job.Done.Wait();
            job.Done.Dispose();
            return job.Result;
        }


        public void Stop()
        {
            var job = new Job { IsStop = true };
            lock (_gate)
            {
                _queue.Enqueue(job);
                Monitor.Pulse(_gate);
            }
            job.Done.Wait(TimeSpan.FromSeconds(5));
        }


        private void Loop()
        {
            ComInterop.EnsureApartment();

            while (true)
            {
                Job? job = null;
                var idle = false;

                lock (_gate)
                {
                    if (_queue.Count == 0 && !Monitor.Wait(_gate, _idleRelease)) idle = _queue.Count == 0;
                    if (_queue.Count > 0) job = _queue.Dequeue();

                    if (job is { IsStop: true })
                    {
                        _stopped = true;
                        while (_queue.Count > 0)
                        {
                            var late = _queue.Dequeue();
                            late.Result = IGStatus.Canceled;
                            late.Done.Set();
                        }
                    }
                }

                if (job is null)
                {
                    if (idle) ReleasePlayers();
                    continue;
                }

                if (job.IsStop)
                {
                    ReleasePlayers();
                    job.Done.Set();
                    return;
                }

                Execute(job);
                job.Done.Set();
            }
        }


        private void Execute(Job job)
        {
            try
            {
                var player = Find(job.Path, job.Timeline!);
                if (player is null)
                {
                    player = AnimPlayer.Open(job.Path, job.Timeline!, out var status);
                    if (player is null)
                    {
                        job.Result = status;
                        return;
                    }

                    _players.Add(player);
                    while (_players.Count > MAX_PLAYERS)
                    {
                        _players[0].Dispose();
                        _players.RemoveAt(0);
                    }
                }

                job.Result = player.Render(job.Index, (IGPixelBuffer*)job.OutBuf, (void*)job.Cancellation);
            }
            catch (OutOfMemoryException)
            {
                job.Result = IGStatus.OutOfMemory;
            }
            catch (Exception ex)
            {
                HostChannel.Log(4, $"WicCodec: animation frame {job.Index} failed. {ex}");
                job.Result = IGStatus.Internal;
            }
        }


        /// <summary>The open player for this file version, or <c>null</c>; a player for an older version is dropped.</summary>
        private AnimPlayer? Find(string path, AnimTimeline timeline)
        {
            for (var i = 0; i < _players.Count; i++)
            {
                var player = _players[i];
                if (!string.Equals(player.Path, path, StringComparison.Ordinal)) continue;

                _players.RemoveAt(i);
                if (!ReferenceEquals(player.Timeline, timeline))
                {
                    player.Dispose();
                    return null;
                }

                _players.Add(player);
                return player;
            }
            return null;
        }


        private void ReleasePlayers()
        {
            // An exception escaping this thread would take the whole host process down.
            foreach (var player in _players)
            {
                try { player.Dispose(); } catch { }
            }
            _players.Clear();
        }
    }
}
