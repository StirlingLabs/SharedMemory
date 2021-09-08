// SharedMemory (File: SharedMemoryTests\RpcBufferTests.cs)
// Copyright (c) 2020 Justin Stenning
// http://spazzarama.com
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Threading;
using SharedMemory;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharedMemoryTests
{
    [TestFixture]
    public class RpcBufferTests
    {
        string ipcName;
        RpcBuffer ipcMaster;
        RpcBuffer ipcSlave;

        [SetUp]
        public void Initialise()
        {
            ipcName = "MasterSlaveTest" + Guid.NewGuid().ToString();
        }

        [TearDown]
        public void Cleanup()
        {
            ipcMaster?.Dispose();
            ipcSlave?.Dispose();
        }

        public void Plot(double[] times, double max, string title, string xLabel, string yLabel)
        {
            var isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            var dataFilePath = Path.GetTempFileName();
            try
            {
                using (var fs = File.Open(dataFilePath, FileMode.Truncate, FileAccess.Write, FileShare.Read | FileShare.Delete))
                    fs.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<double>(times)));

                var gnuPlotPath = isWin
                    ? File.Exists(@"c:\msys64\mingw64\bin\gnuplot.exe") ? @"c:\msys64\mingw64\bin\gnuplot.exe"
                    : File.Exists(@"c:\msys64\ucrt64\bin\gnuplot.exe") ? @"c:\msys64\ucrt64\bin\gnuplot.exe"
                    : throw new NotSupportedException("GnuPlot is missing.")
                    : File.Exists("/bin/gnuplot")
                        ? "/bin/gnuplot"
                        : File.Exists("/usr/bin/gnuplot")
                            ? "/usr/bin/gnuplot"
                            : throw new NotSupportedException("GnuPlot is missing.");

                using var gnuPlot = new Process
                {
                    StartInfo = new()
                    {
                        FileName = gnuPlotPath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        ErrorDialog = false
                    }
                };

                gnuPlot.ErrorDataReceived += (_, args) => {
                    var s = args.Data;
                    if (s is null) return;
                    var w = TestContext.Error;
                    w.Write(s);
                    w.Flush();

                };

                gnuPlot.Start();

                var input = gnuPlot!.StandardInput;

                var xLength = times.Length;
                input.WriteLine($"set terminal dumb nofeed 72,32 ansi256");
                input.WriteLine($"unset key");
                input.WriteLine($"unset border");
                input.WriteLine($"unset colorbox");
                input.WriteLine($"set title \"{title}\"");
                input.WriteLine($"set xlabel \"{xLabel}\"");
                input.WriteLine($"set ylabel \"{yLabel}\"");
                input.WriteLine($"set logscale y");
                input.WriteLine($"set xrange [0:{xLength}]");
                input.WriteLine($"set yrange [-1:{Math.Pow(10, Math.Ceiling(Math.Log10(max)))}]");
                input.WriteLine($"plot '{dataFilePath}' binary array=({xLength}) format='%double' with dot");
                input.Close();
                gnuPlot.BeginErrorReadLine();

                var r = gnuPlot.StandardOutput;
                var w = TestContext.Out;

                if (isWin) r.ReadLine();
                w.WriteLine(r.ReadToEnd());
                w.Flush();

                gnuPlot.WaitForExit();
            }
            finally
            {
                File.Delete(dataFilePath);
            }
        }

        [Test]
        public void Constructor_MasterSlave_Create()
        {
            ipcMaster = new RpcBuffer(ipcName, (msgId, payload) => { });
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) => { });
        }

        [Test]
        public void Constructor_BufferCapacityOutOfRange()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RpcBuffer(ipcName, 255));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RpcBuffer(ipcName, 1024 * 1024 + 1));
        }

        [Test]
        public void RPC_MasterCallsSlave()
        {
            ipcMaster = new RpcBuffer(ipcName);
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) => {
                Assert.IsTrue(payload != null);
                Assert.IsTrue(payload.Length == 2);
                // Add the two bytes together
                return BitConverter.GetBytes((payload[0] + payload[1]));
            });

            var result = ipcMaster.RemoteRequest(new byte[] { 123, 10 });

            Assert.IsTrue(result.Success);
            Assert.AreEqual(123 + 10, BitConverter.ToInt32(result.Data, 0));
        }

        [Test]
        public void RPC_Statistics_Reset()
        {
            ipcMaster = new RpcBuffer(ipcName);
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) => {
                Assert.IsTrue(payload != null);
                Assert.IsTrue(payload.Length == 2);
                // Add the two bytes together
                return BitConverter.GetBytes((payload[0] + payload[1]));
            });

            var result = ipcMaster.RemoteRequest(new byte[] { 123, 10 });

            Assert.IsTrue(result.Success);
            Assert.AreEqual((ulong)1, ipcMaster.Statistics.RequestsSent);
            Assert.AreEqual((ulong)1, ipcSlave.Statistics.RequestsReceived);
            Assert.AreEqual((ulong)1, ipcSlave.Statistics.ResponsesSent);
            Assert.AreEqual((ulong)1, ipcMaster.Statistics.ResponsesReceived);

            ipcMaster.Statistics.Reset();

            var empty = new RpcStatistics();

            Assert.AreEqual(empty.RequestsSent, ipcMaster.Statistics.RequestsSent);
            Assert.AreEqual(empty.ResponsesReceived, ipcMaster.Statistics.ResponsesReceived);
            Assert.AreEqual(empty.ReadingLastMessageSize, ipcMaster.Statistics.ReadingLastMessageSize);
            Assert.AreEqual(empty.WritingLastMessageSize, ipcMaster.Statistics.WritingLastMessageSize);
        }

        [Test]
        public void RPC_MasterCallsSlave_Exception()
        {
            ipcMaster = new RpcBuffer(ipcName);
            ipcSlave = new RpcBuffer(ipcName, async (msgId, payload) => {
                throw new Exception("test exception");
            });

            var result = ipcMaster.RemoteRequest(null);

            Assert.IsFalse(result.Success);
        }

        [Test]
        public void RPC_Bidirectional_Nested()
        {
            ipcMaster = new RpcBuffer(ipcName, async (msgId, payload) => {
                // Ask slave to multiply the two bytes
                return (await ipcMaster.RemoteRequestAsync(new byte[] { 3, 3 }).ConfigureAwait(false)).Data;
            });
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) => {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            // Send request to master from slave
            var result = ipcSlave.RemoteRequest(null);
            Assert.IsTrue(result.Success);
            Assert.AreEqual((3 * 3), result.Data[0]);
        }


        [Test]
        public void RPC_Timeout()
        {
            ipcMaster = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
            }, bufferCapacity: 256);
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                Task.Delay(1000).Wait();
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            var result = ipcMaster.RemoteRequest(new byte[] { 3, 3 }, 100);
            Assert.IsFalse(result.Success);

        }

        [Test]
        public void RPC_Timeout_FireAndForget()
        {
            ipcMaster = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
            }, bufferCapacity: 256);
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                Task.Delay(1000).Wait();
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            var result = ipcMaster.RemoteRequest(new byte[] { 3, 3 }, 0);
            Assert.IsFalse(result.Success);

        }

        [Test]
        public void RPC_LoadTest_5k_Small([Values(5000)] int count)
        {
            ipcMaster = new RpcBuffer(ipcName, async (msgId, payload) => { }, bufferCapacity: 256);
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) => {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            var times = new double[count];
            var max = double.MinValue;
            var min = double.MaxValue;
            var sum = default(TimeSpan);
            // Send request to slave from master
            for (var i = 0; i < count; i++)
            {
                var watch = Stopwatch.StartNew();
                var result = ipcMaster.RemoteRequest(new byte[] { 3, 3 }, 1000);
                var elapsed = watch.Elapsed;
                
                Assert.IsTrue(result.Success);
                Assert.AreEqual((3 * 3), result.Data[0]);
                
                sum += watch.Elapsed;
                var ms = elapsed.TotalMilliseconds;
                times[i] = ms;
                if (ms > max) max = ms;
                if (ms < min) min = ms;
            }
            var avg = sum / count;
            var avgMs = avg.TotalMilliseconds;
            var stdDev = Math.Sqrt(times.Sum(d => Math.Pow(d - avgMs, 2)) / (count - 1));

            //Assert.IsTrue(watch.ElapsedMilliseconds < 1000);
            TestContext.WriteLine($"Sum {sum.TotalSeconds:F3}s, Avg {avg.TotalMilliseconds:F3}ms, Sd {stdDev:F3}ms, Max {max:F3}ms, Min {min:F3}ms");
            try { Plot(times, max, nameof(RPC_LoadTest_5k_Small), "Call", "Time (ms)"); }
            catch (InvalidOperationException ex) { TestContext.WriteLine(ex.ToString()); }
        }
        
        [Test]
        public void RPC_LoadTest_5k_Small_Multi_Thread([Values(10)]int outerCount, [Values(5000)]int innerCount)
        {
            // Warmup the Theadpool
            ThreadPool.SetMinThreads(15, 10);
            
            ipcMaster = new RpcBuffer(ipcName, async (msgId, payload) =>
            {
            }, bufferCapacity: 256);
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) =>
            {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            Stopwatch watch = Stopwatch.StartNew();

            List<Task> tasks = new List<Task>();
            var count = outerCount * innerCount;
            var times = new double[count];
            var max = double.MinValue;
            var min = double.MaxValue;
            var sum = default(TimeSpan);
            for (int i = 0; i < outerCount; i++)
            {
                var c = new StrongBox<int>(i);
                tasks.Add(Task.Run(() => {
                    var boxedI = c.Value;
                    // Send request to slave from master
                    for (var j = 0; j < innerCount; j++)
                    {
                        var watch = Stopwatch.StartNew();
                        var result = ipcMaster.RemoteRequest(new byte[] { 3, 3 });
                        var elapsed = watch.Elapsed;
                        Assert.IsTrue(result.Success);
                        Assert.AreEqual((3 * 3), result.Data[0]);
                        
                        sum += watch.Elapsed;
                        var ms = elapsed.TotalMilliseconds;
                        times[boxedI * innerCount + j] = ms;
                        if (ms > max) max = ms;
                        if (ms < min) min = ms;
                    }       
                }));
                
            }
            var avg = sum / count;
            var avgMs = avg.TotalMilliseconds;
            var stdDev = Math.Sqrt(times.Sum(d => Math.Pow(d - avgMs, 2)) / (count - 1));

            Task.WaitAll(tasks.ToArray());
            watch.Stop();

            //Assert.IsTrue(watch.ElapsedMilliseconds < 1000);
            TestContext.WriteLine($"Sum {sum.TotalSeconds:F3}s, Avg {avg.TotalMilliseconds:F3}ms, Sd {stdDev:F3}ms, Max {max:F3}ms, Min {min:F3}ms");
            try { Plot(times, max, nameof(RPC_LoadTest_5k_Small_Multi_Thread), "Call", "Time (ms)"); }
            catch (InvalidOperationException ex) { TestContext.WriteLine(ex.ToString()); }
        }

        [Test]
        public void RPC_LoadTest_1k_Large([Values(1000)] int count)
        {
            ipcMaster = new RpcBuffer(ipcName, async (msgId, payload) => { }, bufferCapacity: 1025 * 512);
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) => {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            var buf = new byte[1025 * 512];
            buf[0] = 3;
            buf[1] = 3;

            var times = new double[count];
            var max = double.MinValue;
            var min = double.MaxValue;
            var sum = default(TimeSpan);
            // Send request to slave from master
            for (var i = 0; i < count; i++)
            {
                var watch = Stopwatch.StartNew();
                var result = ipcMaster.RemoteRequest(buf, 1000);
                var elapsed = watch.Elapsed;

                Assert.IsTrue(result.Success);
                Assert.AreEqual((3 * 3), result.Data[0]);

                sum += watch.Elapsed;
                var ms = elapsed.TotalMilliseconds;
                times[i] = ms;
                if (ms > max) max = ms;
                if (ms < min) min = ms;
            }
            var avg = sum / count;
            var avgMs = avg.TotalMilliseconds;
            var stdDev = Math.Sqrt(times.Sum(d => Math.Pow(d - avgMs, 2)) / (count - 1));

            //Assert.IsTrue(watch.ElapsedMilliseconds < 2000);
            TestContext.WriteLine($"Sum {sum.TotalSeconds:F3}s, Avg {avg.TotalMilliseconds:F3}ms, Sd {stdDev:F3}ms, Max {max:F3}ms, Min {min:F3}ms");
            try { Plot(times, max, nameof(RPC_LoadTest_1k_Large), "Call", "Time (ms)"); }
            catch (InvalidOperationException ex) { TestContext.WriteLine(ex.ToString()); }
        }

        [Test]
        public void RPC_LoadTest_NestedCalls([Values(100)] int count)
        {
            ipcMaster = new RpcBuffer(ipcName, async (msgId, payload) => {
                // Ask slave to multiply the two bytes
                return (await ipcMaster.RemoteRequestAsync(new byte[] { 3, 3 }).ConfigureAwait(false)).Data;
            });
            ipcSlave = new RpcBuffer(ipcName, (msgId, payload) => {
                return new byte[] { (byte)(payload[0] * payload[1]) };
            });

            var times = new double[count];
            var max = double.MinValue;
            var min = double.MaxValue;
            var sum = default(TimeSpan);
            // Send request to master from slave
            for (var i = 0; i < count; i++)
            {
                var watch = Stopwatch.StartNew();
                var result = ipcSlave.RemoteRequest();
                var elapsed = watch.Elapsed;

                Assert.IsTrue(result.Success);
                Assert.AreEqual((3 * 3), result.Data[0]);

                sum += watch.Elapsed;
                var ms = elapsed.TotalMilliseconds;
                times[i] = ms;
                if (ms > max) max = ms;
                if (ms < min) min = ms;
            }
            var avg = sum / count;
            var avgMs = avg.TotalMilliseconds;
            var stdDev = Math.Sqrt(times.Sum(d => Math.Pow(d - avgMs, 2)) / (count - 1));

            //Assert.IsTrue(watch.ElapsedMilliseconds < 1000);
            TestContext.WriteLine($"Sum {sum.TotalSeconds:F3}s, Avg {avg.TotalMilliseconds:F3}ms, Sd {stdDev:F3}ms, Max {max:F3}ms, Min {min:F3}ms");
            try { Plot(times, max, nameof(RPC_LoadTest_NestedCalls), "Call", "Time (ms)"); }
            catch (InvalidOperationException ex) { TestContext.WriteLine(ex.ToString()); }
        }

        [Test]
        public void RPC_SlaveCallsMasterAfterClosed_Exception()
        {
            ipcMaster = new RpcBuffer(ipcName, async (msgId, payload) => { });

            ipcSlave = new RpcBuffer(ipcName);

            ipcSlave.RemoteRequest(null);

            ipcMaster.Dispose();

            Assert.Throws<InvalidOperationException>(() => ipcSlave.RemoteRequest(null));
        }
    }
}
