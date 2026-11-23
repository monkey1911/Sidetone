// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Web streams: the codeplug's web_streams (per zone), audio feeds such as Broadcastify or Icecast played
* beside the channels, as the Windows console's stream chips. MP3 only, decoded by NLayer (plain C#, so
* the same on every platform), then mixed to mono and resampled to 8 kHz like the radio audio. As the
* Windows console: 3 connection attempts 5 s apart, then failed; RX while there's audio (its thresholds),
* held 1.4 s. A refusal (401, 403, 404, not MP3) fails at once, since trying again can't help.
*
* The log shows a stream's name and host only: feed URLs often carry a token, and the codeplug may hold
* a password for it.
*/

using System.Net;
using dvmconsole;
using System.Net.Http.Headers;
using NLayer;

namespace Sidetone.Core
{
    /// <summary>What a web stream is doing, as its card shows it.</summary>
    public enum StreamState { Off, Connecting, Idle, Receiving, Failed }

    /// <summary>
    /// Plays one web stream on a worker thread. The console reads its state from the UI thread.
    /// </summary>
    internal sealed class WebStreamPlayer : IDisposable
    {
        public const int Attempts = 3;
        public static readonly TimeSpan ActivityHold = TimeSpan.FromMilliseconds(1400);
        private const double ActivityRms = 0.0035 * 32768;
        private const int ActivityPeak = 650;
        /// <summary>Nothing that looks like MP3 within this much: it isn't one.</summary>
        internal const int SyncLimit = 64 * 1024;

        /// <summary>Between connection attempts (shorter in the self-test).</summary>
        internal static TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
        /// <summary>No data for this long on a playing stream: it's dead, connect again (shorter in the self-test).</summary>
        internal static TimeSpan QuietTimeout = TimeSpan.FromSeconds(30);

        private readonly Codeplug.WebStream cfg;
        private readonly Func<IAudioOutput> openOutput;
        private readonly Func<double> gain;
        private readonly Action<string> log;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly string label;
        private Thread thread;
        private IAudioOutput output;
        private volatile bool reopen;
        private long lastBlockTicks;
        private long played, lastActive = long.MinValue / 2;     // in samples: the hold is audio time, as a feed arrives in bursts

        public volatile StreamState State = StreamState.Off;
        public volatile int Attempt;
        public volatile string Reason;

        /// <param name="openOutput">Opens the output to play to (null for none); called on the worker thread.</param>
        /// <param name="gain">The volume to apply now (0 when muted).</param>
        public WebStreamPlayer(Codeplug.WebStream cfg, Func<IAudioOutput> openOutput, Func<double> gain, Action<string> log)
        {
            this.cfg = cfg;
            this.openOutput = openOutput;
            this.gain = gain;
            this.log = log;
            label = $"stream {cfg.Name} ({HostOf(cfg.Url)})";
        }

        /// <summary>The URL's host, the only part of it ever logged or shown.</summary>
        public static string HostOf(string url) =>
            Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri u) && !string.IsNullOrEmpty(u.Host) ? u.Host : "no address";

        /// <summary>When the last block was played (UTC): a feed that stalls isn't receiving.</summary>
        public DateTime LastBlockUtc => new DateTime(Interlocked.Read(ref lastBlockTicks), DateTimeKind.Utc);

        public void Start()
        {
            State = StreamState.Connecting;
            thread = new Thread(Run) { IsBackground = true, Name = "web stream" };
            thread.Start();
        }

        /// <summary>The tab's output changed: the next block goes to the new one.</summary>
        public void ReopenOutput() => reopen = true;

        public void Dispose()
        {
            stop.Cancel();
            thread?.Join(TimeSpan.FromSeconds(2));
            State = StreamState.Off;
        }

        /// <summary>A failure trying again won't fix.</summary>
        private sealed class RefusedException : Exception
        {
            public RefusedException(string message) : base(message) { }
        }

        private void Run()
        {
            int attempt = 0;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    Attempt = ++attempt;
                    State = StreamState.Connecting;
                    bool played = false;
                    string why;
                    try { why = PlayOnce(ref played); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                    catch (RefusedException ex)
                    {
                        Fail(ex.Message);
                        return;
                    }
                    catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is InvalidDataException || ex is OperationCanceledException)
                    {
                        why = Plain(ex);
                    }
                    if (stop.IsCancellationRequested)
                        return;

                    if (played)
                        attempt = 0;        // it was playing: a drop starts the attempts again
                    if (!played && attempt >= Attempts)
                    {
                        Fail($"{why} ({Attempts} attempts)");
                        return;
                    }
                    State = StreamState.Connecting;
                    log($"{label}: {why}; trying again in {RetryDelay.TotalSeconds:0.#} s");
                    stop.Token.WaitHandle.WaitOne(RetryDelay);
                }
            }
            finally
            {
                CloseOutput();
            }
        }

        private void Fail(string why)
        {
            Reason = why;
            State = StreamState.Failed;
            log($"{label}: failed, {why}");
        }

        private static string Plain(Exception ex) => ex switch
        {
            HttpRequestException h when h.InnerException is System.Net.Sockets.SocketException s => $"can't connect ({s.SocketErrorCode})",
            HttpRequestException h => $"can't connect ({h.Message.TrimEnd('.')})",
            OperationCanceledException => "no answer (timed out)",
            _ => ex.Message.TrimEnd('.')
        };

        /// <summary>One connection: plays until the stream ends or breaks. Returns why it stopped.</summary>
        private string PlayOnce(ref bool played)
        {
            if (!Uri.TryCreate(cfg.Url?.Trim(), UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new RefusedException("its url isn't an http:// or https:// address");

            CancellationToken token = stop.Token;
            using HttpClient client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd($"Sidetone/{DispatchConsole.Version}");
            bool credentials = !string.IsNullOrEmpty(cfg.AuthUsername) || !string.IsNullOrEmpty(cfg.AuthPassword);
            if (credentials)
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{cfg.AuthUsername}:{cfg.AuthPassword}")));

            using CancellationTokenSource answer = CancellationTokenSource.CreateLinkedTokenSource(token);
            answer.CancelAfter(TimeSpan.FromSeconds(15));
            using HttpResponseMessage response = client.Send(request, HttpCompletionOption.ResponseHeadersRead, answer.Token);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    throw new RefusedException(credentials ? "its user name or password was refused (401)" : "it needs a user name and password (401)");
                case HttpStatusCode.Forbidden:
                    throw new RefusedException("refused by the server (403)");
                case HttpStatusCode.NotFound:
                    throw new RefusedException("not found on the server (404)");
            }
            if (!response.IsSuccessStatusCode)
                return $"the server answered {(int)response.StatusCode}";
            string type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (type == "text/html")
                throw new RefusedException("it's a web page (text/html), not an audio stream; check its url");
            if (type != null && (type.StartsWith("audio/") || type.StartsWith("video/") || type.Contains("mpegurl") || type.Contains("ogg"))
                && type != "audio/mpeg" && type != "audio/mp3" && type != "audio/mpeg3" && type != "audio/x-mpeg")
                throw new RefusedException($"it sends {type}; Sidetone plays MP3 streams only");

            using Stream body = response.Content.ReadAsStream(token);
            using ByteCounter counted = new ByteCounter(body, token) { Limit = SyncLimit };
            MpegFile mp3;
            try { mp3 = new MpegFile(counted) { StereoMode = StereoMode.DownmixToMono }; }
            catch (InvalidDataException) { throw new RefusedException("it isn't MP3 audio"); }
            using (mp3)
            {
                if (mp3.SampleRate <= 0)
                    throw new RefusedException("it isn't MP3 audio");
                counted.Limit = long.MaxValue;
                WavFile.StreamResampler resampler = new WavFile.StreamResampler(mp3.SampleRate, PipeWireSink.SampleRate);
                log($"{label}: playing ({mp3.SampleRate / 1000.0:0.#} kHz MP3)");
                State = StreamState.Idle;

                float[] decoded = new float[2048];
                short[] block = new short[160];
                int filled = 0;
                while (!token.IsCancellationRequested)
                {
                    int n = mp3.ReadSamples(decoded, 0, decoded.Length);
                    if (n <= 0)
                        return "the stream ended";
                    played = true;
                    Attempt = 0;
                    foreach (float f in resampler.Push(decoded.AsSpan(0, n)))
                    {
                        block[filled++] = (short)Math.Clamp(Math.Round(f * 32767.0), -32768, 32767);
                        if (filled == block.Length)
                        {
                            Play(block);
                            filled = 0;
                        }
                    }
                }
                return "stopped";
            }
        }

        /// <summary>One 20 ms block: activity, then the volume, then the output.</summary>
        private void Play(short[] block)
        {
            double sum = 0;
            int peak = 0;
            foreach (short s in block)
            {
                sum += (double)s * s;
                peak = Math.Max(peak, Math.Abs((int)s));
            }
            played += block.Length;
            Interlocked.Exchange(ref lastBlockTicks, DateTime.UtcNow.Ticks);
            if (Math.Sqrt(sum / block.Length) >= ActivityRms || peak >= ActivityPeak)
            {
                lastActive = played;
                State = StreamState.Receiving;
            }
            else if (State == StreamState.Receiving && played - lastActive > ActivityHold.TotalSeconds * PipeWireSink.SampleRate)
                State = StreamState.Idle;

            if (reopen || output == null)
            {
                reopen = false;
                CloseOutput();
                try { output = openOutput(); }
                catch (Exception ex) { log($"{label}: no audio output ({ex.Message})"); }
                if (output != null)
                    output.Failed += msg => log($"{label}: {msg}");
            }
            if (output == null)
                return;
            short[] copy = (short[])block.Clone();      // the output may keep it
            AudioMath.ApplyGain(copy, gain());
            output.Write(copy);
        }

        private void CloseOutput()
        {
            try { output?.Dispose(); }
            catch { /* going anyway */ }
            output = null;
        }

        /// <summary>
        /// The response body as NLayer wants it: it asks for Position even when it can't seek. Reads give up
        /// past Limit (no MP3 found in that much).
        /// </summary>
        private sealed class ByteCounter : Stream
        {
            private readonly Stream inner;
            private readonly CancellationToken token;
            private long read;
            public long Limit = long.MaxValue;

            public ByteCounter(Stream inner, CancellationToken token)
            {
                this.inner = inner;
                this.token = token;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (read > Limit)
                    throw new InvalidDataException("no MP3 audio found");
                // a connection left half-open (a phone changing networks) sends nothing and never closes: give up and reconnect
                using CancellationTokenSource quiet = CancellationTokenSource.CreateLinkedTokenSource(token);
                quiet.CancelAfter(QuietTimeout);
                int n = inner.ReadAsync(buffer.AsMemory(offset, count), quiet.Token).AsTask().GetAwaiter().GetResult();
                read += n;
                return n;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => read; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        } // private sealed class ByteCounter
    } // internal sealed class WebStreamPlayer
}
