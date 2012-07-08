﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SignalR.Hosting.Memory;
using SignalR.Transports;

namespace SignalR.Stress
{
    class Program
    {
        private static Timer _rateTimer;
        private static bool _measuringRate;
        private static Stopwatch _sw = Stopwatch.StartNew();

        private static double _receivesPerSecond;
        private static double _peakReceivesPerSecond;
        private static double _avgReceivesPerSecond;
        private static long _received;
        private static long _avgLastReceivedCount;
        private static long _lastReceivedCount;

        private static double _sendsPerSecond;
        private static double _peakSendsPerSecond;
        private static double _avgSendsPerSecond;
        private static long _sent;
        private static long _avgLastSendsCount;
        private static long _lastSendsCount;

        private static DateTime _avgCalcStart;
        private static long _rate = 1;
        private static int _runs = 0;
        private static int _step = 1;
        private static int _stepInterval = 1000000;
        private static int _clients = 1;
        private static int _clientsRunning = 0;
        private static int _senders = 1;
        private static Exception _exception;

        public static long TotalRate
        {
            get
            {
                return _rate * _clients;
            }
        }

        static void Main(string[] args)
        {
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            ThreadPool.SetMinThreads(32, 32);

            // RunBusTest();
            // RunConnectionTest();
            RunMemoryHost();

            Console.ReadLine();
        }

        private static void RunConnectionTest()
        {            
            string payload = GetPayload();

            var dr = new DefaultDependencyResolver();
            MeasureStats((MessageBus)dr.Resolve<INewMessageBus>());
            var connectionManager = new ConnectionManager(dr);
            var context = connectionManager.GetConnectionContext<StressConnection>();

            for (int i = 0; i < _clients; i++)
            {
                ThreadPool.QueueUserWorkItem(state =>
                {
                    Interlocked.Increment(ref _clientsRunning);
                    var transportConnection = (ITransportConnection)context.Connection;
                    transportConnection.Receive(null, (ex, r) =>
                    {
                        Interlocked.Add(ref _received, r.Messages.Count);
                        Interlocked.Add(ref _avgLastReceivedCount, r.Messages.Count);
                        return TaskAsyncHelper.True;
                    });

                }, i);
            }

            for (var i = 1; i <= _senders; i++)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    StartSendLoop(i.ToString(), (source, key, value) => context.Connection.Broadcast(value), payload);
                });
            }
        }

        private static void RunMemoryHost()
        {
            var host = new MemoryHost();
            host.MapConnection<StressConnection>("/echo");

            string payload = GetPayload();

            MeasureStats((MessageBus)host.DependencyResolver.Resolve<INewMessageBus>());

            Action<PersistentResponse> handler = (r) =>
            {
                Interlocked.Add(ref _received, r.Messages.Count);
                Interlocked.Add(ref _avgLastReceivedCount, r.Messages.Count);
            };

            LongPollingTransport.SendingResponse += handler;
            ForeverFrameTransport.SendingResponse += handler;

            for (int i = 0; i < _clients; i++)
            {
                ThreadPool.QueueUserWorkItem(state =>
                {
                    Interlocked.Increment(ref _clientsRunning);
                    string connectionId = state.ToString();

                    // LongPollingLoop(host, connectionId);
                    ProcessRequest(host, "serverSentEvents", connectionId);
                }, i);
            }

            for (var i = 1; i <= _senders; i++)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    var context = host.ConnectionManager.GetConnectionContext<StressConnection>();
                    StartSendLoop(i.ToString(), (source, key, value) => context.Connection.Broadcast(value), payload);
                });
            }
        }

        private static void LongPollingLoop(MemoryHost host, string connectionId)
        {            
            LongPoll:
            var task = ProcessRequest(host, "longPolling", connectionId);

            if (task.IsCompleted)
            {
                task.Wait();

                goto LongPoll;
            }

            task.ContinueWith(t => LongPollingLoop(host, connectionId));
        }

        private static Task ProcessRequest(MemoryHost host, string transport, string connectionId)
        {
            return host.ProcessRequest("http://foo/echo/connect?transport=" + transport + "&connectionId=" + connectionId, request => { }, null);
        }

        private static void RunBusTest()
        {
            var resolver = new DefaultDependencyResolver();
            var bus = new MessageBus(resolver);
            string payload = GetPayload();

            MeasureStats(bus);

            for (int i = 0; i < _clients; i++)
            {
                var subscriber = new Subscriber(new[] { "a", "b", "c" });
                ThreadPool.QueueUserWorkItem(_ => StartClientLoop(bus, subscriber));
            }

            for (var i = 1; i <= _senders; i++)
            {
                ThreadPool.QueueUserWorkItem(_ => StartSendLoop(i.ToString(), bus.Publish, payload));
            }
        }

        private static void StartSendLoop(string clientId, Func<string, string, object, Task> publish, string payload)
        {
            while (_exception == null)
            {
                long old = _rate;
                var interval = TimeSpan.FromMilliseconds((1000.0 / _rate) * _senders);
                while (Interlocked.Read(ref _rate) == old && _exception == null)
                {
                    try
                    {
                        publish(clientId, "a", payload).Wait();
                        Interlocked.Increment(ref _sent);
                        Interlocked.Increment(ref _avgLastSendsCount);

                        // Thread.Sleep(interval);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Exchange(ref _exception, ex);
                    }
                }
            }
        }

        private static string GetPayload(int n = 32)
        {
            return Encoding.UTF8.GetString(Enumerable.Range(0, n).Select(i => (byte)i).ToArray());
        }

        static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            Interlocked.Exchange(ref _exception, e.Exception);
            e.SetObserved();
        }

        private static void StartClientLoop(MessageBus bus, ISubscriber subscriber)
        {
            Interlocked.Increment(ref _clientsRunning);
            ReceiveLoop(bus, subscriber);
        }

        private static void ReceiveLoop(MessageBus bus, ISubscriber subscriber)
        {
            try
            {
                bus.Subscribe(subscriber, null, (ex, result) =>
                {
                    if (ex != null)
                    {
                        Interlocked.Exchange(ref _exception, ex);
                        return TaskAsyncHelper.False;
                    }
                    else
                    {
                        Interlocked.Add(ref _received, result.TotalCount);
                        Interlocked.Add(ref _avgLastReceivedCount, result.TotalCount);
                    }

                    return TaskAsyncHelper.True;
                });
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _exception, ex);
            }
        }

        public static void MeasureStats(MessageBus bus)
        {
            _sw.Start();
            _avgCalcStart = DateTime.UtcNow;
            var resultsPath = Guid.NewGuid().ToString() + ".csv";
            // File.WriteAllText(resultsPath, "Target Rate, RPS, Peak RPS, Avg RPS\n");

            _rateTimer = new Timer(_ =>
            {
                if (_measuringRate)
                {
                    return;
                }
                _measuringRate = true;

                try
                {
                    var now = DateTime.UtcNow;
                    var timeDiffSecs = _sw.Elapsed.TotalSeconds;

                    _sw.Restart();

                    if (timeDiffSecs <= 0)
                    {
                        return;
                    }

                    if (_exception != null)
                    {
                        Console.WriteLine("Failed With:\r\n {0}", _exception.GetBaseException());
                        _rateTimer.Change(-1, -1);
                        _rateTimer.Dispose();
                        _rateTimer = null;
                        return;
                    }

                    Console.Clear();
                    Console.WriteLine("Started {0} of {1} clients", _clientsRunning, _clients);

                    Console.WriteLine("Total Rate: {0} (mps) = {1} (mps) * {2} (clients)", TotalRate, _rate, _clients);
                    Console.WriteLine();

                    // Sends
                    var sends = Interlocked.Read(ref _sent);
                    var sendsDiff = sends - _lastSendsCount;
                    var sendsPerSec = sendsDiff / timeDiffSecs;
                    _sendsPerSecond = sendsPerSec;

                    _lastSendsCount = sends;

                    Console.WriteLine("----- SENDS -----");

                    var s1 = Math.Max(0, _rate - _sendsPerSecond);
                    Console.WriteLine("SPS: {0:0.000} (diff: {1:0.000}, {2:0.00}%)", _sendsPerSecond, s1, s1 * 100.0 / _rate);
                    var s2 = Math.Max(0, _rate - _peakSendsPerSecond);
                    Console.WriteLine("Peak SPS: {0:0.000} (diff: {1:0.000} {2:0.00}%)", _peakSendsPerSecond, s2, s2 * 100.0 / _rate);
                    var s3 = Math.Max(0, _rate - _avgSendsPerSecond);
                    Console.WriteLine("Avg SPS: {0:0.000} (diff: {1:0.000} {2:0.00}%)", _avgSendsPerSecond, s3, s3 * 100.0 / _rate);
                    Console.WriteLine();

                    if (sendsPerSec < long.MaxValue && sendsPerSec > _peakSendsPerSecond)
                    {
                        Interlocked.Exchange(ref _peakSendsPerSecond, sendsPerSec);
                    }

                    _avgSendsPerSecond = _avgLastSendsCount / (now - _avgCalcStart).TotalSeconds;

                    // Receives
                    var recv = Interlocked.Read(ref _received);
                    var recvDiff = recv - _lastReceivedCount;
                    var recvPerSec = recvDiff / timeDiffSecs;
                    _receivesPerSecond = recvPerSec;

                    _lastReceivedCount = recv;

                    Console.WriteLine("----- RECEIVES -----");

                    var d1 = Math.Max(0, TotalRate - _receivesPerSecond);
                    Console.WriteLine("RPS: {0:0.000} (diff: {1:0.000}, {2:0.00}%)", _receivesPerSecond, d1, d1 * 100.0 / TotalRate);
                    var d2 = Math.Max(0, TotalRate - _peakReceivesPerSecond);
                    Console.WriteLine("Peak RPS: {0:0.000} (diff: {1:0.000} {2:0.00}%)", _peakReceivesPerSecond, d2, d2 * 100.0 / TotalRate);
                    var d3 = Math.Max(0, TotalRate - _avgReceivesPerSecond);
                    Console.WriteLine("Avg RPS: {0:0.000} (diff: {1:0.000} {2:0.00}%)", _avgReceivesPerSecond, d3, d3 * 100.0 / TotalRate);
                    var d4 = Math.Max(0, _sendsPerSecond - _receivesPerSecond);
                    Console.WriteLine("Actual RPS: {0:0.000} (diff: {1:0.000} {2:0.00}%)", _receivesPerSecond, d4, d4 * 100.0 / _sendsPerSecond);

                    if (bus != null)
                    {
                        Console.WriteLine();
                        Console.WriteLine("----- MESSAGE BUS -----");
                        Console.WriteLine("Allocated Workers: {0}", bus.AllocatedWorkers);
                        Console.WriteLine("BusyWorkers Workers: {0}", bus.BusyWorkers);
                    }

                    if (recvPerSec < long.MaxValue && recvPerSec > _peakReceivesPerSecond)
                    {
                        Interlocked.Exchange(ref _peakReceivesPerSecond, recvPerSec);
                    }

                    _avgReceivesPerSecond = _avgLastReceivedCount / (now - _avgCalcStart).TotalSeconds;

                    // File.AppendAllText(resultsPath, String.Format("{0}, {1}, {2}, {3}\n", TotalRate, _receivesPerSecond, _peakReceivesPerSecond, _avgReceivesPerSecond));

                    if (_runs > 0 && _runs % _stepInterval == 0)
                    {
                        _avgCalcStart = DateTime.UtcNow;
                        Interlocked.Exchange(ref _avgLastReceivedCount, 0);
                        Interlocked.Exchange(ref _avgLastSendsCount, 0);
                        long old = Interlocked.Read(ref _rate);
                        long @new = old + _step;
                        while (Interlocked.Exchange(ref _rate, @new) == old) { }
                    }

                    _runs++;

                }
                finally
                {
                    _measuringRate = false;
                }
            }, null, 1000, 1000);
        }

        public class StressConnection : PersistentConnection
        {

        }
    }
}
