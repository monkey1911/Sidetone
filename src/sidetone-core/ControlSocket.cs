// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone - an IP dispatch console for DVMProject FNE networks.
*
* Control socket: a Unix domain socket that only the user can open, through which other programs
* (a desktop hotkey, a script, a footswitch helper) control the running console. The protocol is
* plain text, one command per line, one reply line per command:
*
*   ptt down     start transmitting on the focused channel
*   ptt up       stop transmitting
*   ptt toggle   start or stop
*   ptt hold     start transmitting until this connection closes (or "ptt up" from anywhere)
*   status       one line describing the console
*   help         the list of commands
*
* Replies start with "ok" or "error". `sidetone-cli ptt ...` is the ready-made client.
*/

using System.Net.Sockets;
using System.Text;

namespace Sidetone.Core
{
    /// <summary>
    /// One client connection to the control socket.
    /// </summary>
    public sealed class ControlSession
    {
        private static int nextId;
        private readonly Socket socket;
        private int closed;

        internal ControlSession(Socket socket)
        {
            this.socket = socket;
            Id = Interlocked.Increment(ref nextId);
        }

        /// <summary>Connection number, for logging.</summary>
        public int Id { get; }

        /// <summary>Raised once when the connection ends, whichever side ends it.</summary>
        public event Action Closed;

        /// <summary>Ends the connection from the console's side (a held PTT that has been released).</summary>
        public void Close()
        {
            try { socket.Shutdown(SocketShutdown.Both); } catch { /* already gone */ }
            try { socket.Close(); } catch { /* already gone */ }
            OnClosed();
        }

        internal void OnClosed()
        {
            if (Interlocked.Exchange(ref closed, 1) == 0)
                Closed?.Invoke();
        }
    } // public sealed class ControlSession

    /// <summary>
    /// Listens on the control socket and passes each command line to a handler.
    /// </summary>
    public sealed class ControlServer : IDisposable
    {
        /// <summary>Longest command line accepted.</summary>
        public const int MaxLine = 256;

        private readonly string path;
        private readonly Func<string, ControlSession, string> handler;
        private readonly List<ControlSession> sessions = new List<ControlSession>();
        private Socket listener;
        private volatile bool stopping;

        /// <summary>
        /// The default socket: $XDG_RUNTIME_DIR/sidetone.sock (a folder only the user can open),
        /// or a private folder under /tmp when XDG_RUNTIME_DIR isn't set.
        /// </summary>
        public static string DefaultPath
        {
            get
            {
                string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
                if (!string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime))
                    return Path.Combine(runtime, "sidetone.sock");
                return Path.Combine(Path.GetTempPath(), $"sidetone-{Environment.UserName}", "sidetone.sock");
            }
        }

        /// <summary>The socket's path.</summary>
        public string SocketPath => path;

        /// <param name="handler">Called on a connection's thread with each command line; returns the reply line.</param>
        public ControlServer(string path, Func<string, ControlSession, string> handler)
        {
            this.path = path;
            this.handler = handler;
        }

        /// <summary>
        /// Creates the socket and starts accepting connections. Throws InvalidOperationException
        /// when another running console already answers on the same path.
        /// </summary>
        public void Start()
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            if (File.Exists(path))
            {
                if (ControlClient.IsAlive(path))
                    throw new InvalidOperationException($"another Sidetone console is already using {path}");
                File.Delete(path);      // left over from a console that didn't shut down cleanly
            }

            Socket s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                s.Bind(new UnixDomainSocketEndPoint(path));
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                s.Listen(8);
            }
            catch
            {
                s.Dispose();
                throw;
            }
            listener = s;
            new Thread(AcceptLoop) { IsBackground = true, Name = "sidetone control" }.Start();
        }

        private void AcceptLoop()
        {
            while (!stopping)
            {
                Socket client;
                try { client = listener.Accept(); }
                catch { if (stopping) return; Thread.Sleep(100); continue; }

                ControlSession session = new ControlSession(client);
                lock (sessions)
                    sessions.Add(session);
                new Thread(() => Serve(client, session)) { IsBackground = true, Name = $"sidetone control {session.Id}" }.Start();
            }
        }

        private void Serve(Socket client, ControlSession session)
        {
            try
            {
                using NetworkStream stream = new NetworkStream(client, ownsSocket: false);
                StringBuilder line = new StringBuilder();
                byte[] buf = new byte[512];
                int n;
                while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        char ch = (char)buf[i];
                        if (ch == '\n')
                        {
                            string reply = Handle(line.ToString().Trim(), session);
                            line.Clear();
                            byte[] out1 = Encoding.UTF8.GetBytes(reply + "\n");
                            stream.Write(out1, 0, out1.Length);
                        }
                        else if (ch != '\r')
                        {
                            line.Append(ch);
                            if (line.Length > MaxLine)
                                return;     // not a command; drop the connection
                        }
                    }
                }
            }
            catch
            {
                // connection reset or closed by the console
            }
            finally
            {
                try { client.Close(); } catch { /* ignore */ }
                lock (sessions)
                    sessions.Remove(session);
                session.OnClosed();
            }
        }

        private string Handle(string line, ControlSession session)
        {
            if (line.Length == 0)
                return "error: empty command (try 'help')";
            try
            {
                string reply = handler(line, session) ?? "error: no reply";
                return reply.Replace('\n', ' ');
            }
            catch (Exception ex)
            {
                return $"error: {ex.Message}";
            }
        }

        public void Dispose()
        {
            stopping = true;
            try { listener?.Close(); } catch { /* ignore */ }
            List<ControlSession> open;
            lock (sessions)
                open = new List<ControlSession>(sessions);
            foreach (ControlSession s in open)
                s.Close();
            try
            {
                if (listener != null && File.Exists(path))
                    File.Delete(path);
            }
            catch { /* ignore */ }
        }
    } // public sealed class ControlServer

    /// <summary>
    /// Client side of the control socket (sidetone-cli ptt).
    /// </summary>
    public static class ControlClient
    {
        /// <summary>Whether something answers on the socket.</summary>
        public static bool IsAlive(string path)
        {
            try
            {
                using Socket s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                s.Connect(new UnixDomainSocketEndPoint(path));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Connects, sends one command and returns the reply. With <paramref name="holdUntil"/>, keeps the
        /// connection open after the reply until the console closes it or the token is cancelled.
        /// </summary>
        /// <param name="onReply">Called with the reply as soon as it arrives (before a hold starts waiting).</param>
        public static string Send(string path, string command, CancellationToken holdUntil = default, bool hold = false, Action<string> onReply = null)
        {
            using Socket s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                s.Connect(new UnixDomainSocketEndPoint(path));
            }
            catch (SocketException)
            {
                throw new InvalidOperationException($"no Sidetone console is listening on {path} (is the console running?)");
            }

            using NetworkStream stream = new NetworkStream(s, ownsSocket: false);
            byte[] cmd = Encoding.UTF8.GetBytes(command.Trim() + "\n");
            stream.Write(cmd, 0, cmd.Length);

            string reply = ReadLine(stream) ?? "error: the console closed the connection";
            onReply?.Invoke(reply);
            if (hold && reply.StartsWith("ok", StringComparison.Ordinal))
            {
                // the console closes the connection when the transmission ends; closing it ourselves releases PTT
                using CancellationTokenRegistration reg = holdUntil.Register(() => { try { s.Shutdown(SocketShutdown.Both); } catch { } });
                try { while (ReadLine(stream) != null) { } } catch { /* closed */ }
            }
            return reply;
        }

        private static string ReadLine(Stream stream)
        {
            List<byte> bytes = new List<byte>();
            int b;
            while ((b = stream.ReadByte()) >= 0)
            {
                if (b == '\n')
                    return Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add((byte)b);
            }
            return bytes.Count > 0 ? Encoding.UTF8.GetString(bytes.ToArray()) : null;
        }
    } // public static class ControlClient
}
