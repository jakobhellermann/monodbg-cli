using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;

// CLI front-end for a stateful mono soft-debugger session. Subcommands are thin clients that
// talk to a background daemon (Session) over a per-target unix socket; the daemon holds the one
// VM connection to the agent and keeps it suspended at a breakpoint between commands.
//   monodbg break <Type.Method> [--asm N] [--wait] [--timeout S]
//   monodbg inspect [<expr>] [--frame N]  no expr: list inspectable roots (this/args/locals)
//                                           expr = this | argName | this.field.subfield (fields only)
//   monodbg bp | unbreak <#|Type.Method> | unbreak --all
//   monodbg stack | continue | status | quit
namespace MonoDbg
{
    static class Program
    {
        static string host = "127.0.0.1";
        static int port = 10001;
        static string Sock => $"/tmp/monodbg-{host}-{port}.sock";

        static int Main(string[] argv)
        {
            var a = new List<string>(argv);
            for (int i = 0; i < a.Count;)
            {
                if (a[i] == "--host") { host = a[i + 1]; a.RemoveRange(i, 2); }
                else if (a[i] == "--port") { port = int.Parse(a[i + 1]); a.RemoveRange(i, 2); }
                else i++;
            }
            if (a.Count == 0) { Usage(); return 64; }
            string cmd = a[0]; a.RemoveAt(0);
            switch (cmd)
            {
                case "__daemon": return Session.Run(host, port, Sock);
                case "break": return CmdBreak(a);
                case "bp": case "breaks": case "breakpoints": return Send(new() { ["cmd"] = "breakpoints" });
                case "unbreak": case "rmbreak": return CmdUnbreak(a);
                case "inspect": return CmdInspect(a);
                case "stack": return Send(new() { ["cmd"] = "stack" });
                case "continue": case "cont": return Send(new() { ["cmd"] = "continue" });
                case "status": return Send(new() { ["cmd"] = "status" });
                case "quit": case "detach": return Send(new() { ["cmd"] = "quit" });
                default: Usage(); return 64;
            }
        }

        static int CmdBreak(List<string> a)
        {
            string target = null, asm = null; bool wait = false; int timeout = 120;
            for (int i = 0; i < a.Count; i++)
            {
                switch (a[i])
                {
                    case "--asm": asm = a[++i]; break;
                    case "--wait": wait = true; break;
                    case "--timeout": timeout = int.Parse(a[++i]); break;
                    default: target = a[i]; break;
                }
            }
            if (target == null) { Console.Error.WriteLine("usage: monodbg break <Type.Method> [--asm N] [--wait] [--timeout S]"); return 64; }
            if (!EnsureDaemon()) { Console.Error.WriteLine($"[monodbg] could not start daemon (agent on {host}:{port}? other debugger attached?)"); return 3; }
            var req = new Dictionary<string, string> { ["cmd"] = "break", ["target"] = target, ["wait"] = wait ? "1" : "0", ["timeout"] = timeout.ToString() };
            if (asm != null) req["asm"] = asm;
            return Send(req, wait ? timeout + 15 : 15);
        }

        static int CmdUnbreak(List<string> a)
        {
            bool all = false; string target = null;
            for (int i = 0; i < a.Count; i++) { if (a[i] == "--all") all = true; else target = a[i]; }
            if (!all && target == null) { Console.Error.WriteLine("usage: monodbg unbreak <#|Type.Method> | --all"); return 64; }
            var req = new Dictionary<string, string> { ["cmd"] = "unbreak" };
            if (all) req["all"] = "1"; else req["target"] = target;
            return Send(req);
        }

        static int CmdInspect(List<string> a)
        {
            string expr = null; int frame = 0;
            for (int i = 0; i < a.Count; i++) { if (a[i] == "--frame") frame = int.Parse(a[++i]); else expr = a[i]; }
            // no expr: ask the daemon for the frame's inspectable roots instead of bailing with usage
            if (expr == null) return Send(new() { ["cmd"] = "inspect", ["frame"] = frame.ToString() });
            return Send(new() { ["cmd"] = "inspect", ["expr"] = expr, ["frame"] = frame.ToString() });
        }

        static bool EnsureDaemon()
        {
            if (TryConnect(out var s)) { s.Dispose(); return true; }
            var dll = Assembly.GetEntryAssembly().Location;
            var host_ = Environment.ProcessPath ?? "dotnet";
            var log = $"/tmp/monodbg-{host}-{port}.log";
            // ProcessPath is a self-contained apphost (e.g. under `dotnet run`, or running the built
            // exe directly) when its name matches the entry dll's: it already hosts the dll, so don't
            // pass the dll path as an extra arg (that would shift argv and swallow "__daemon").
            bool isApphost = Path.GetFileNameWithoutExtension(host_) == Path.GetFileNameWithoutExtension(dll);
            var target = isApphost ? $"\"{host_}\"" : $"\"{host_}\" \"{dll}\"";
            // setsid + redirected fds: the daemon must not inherit our stdio, or it holds the
            // caller's pipe open and blocks the shell until the daemon exits.
            var psi = new ProcessStartInfo { FileName = "/bin/sh", UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"exec setsid {target} __daemon --host {host} --port {port} >\"{log}\" 2>&1 </dev/null");
            try { Process.Start(psi); } catch (Exception e) { Console.Error.WriteLine("[monodbg] spawn failed: " + e.Message); return false; }
            for (int i = 0; i < 60; i++) { Thread.Sleep(100); if (TryConnect(out var s2)) { s2.Dispose(); return true; } }
            return false;
        }

        static bool TryConnect(out Socket s)
        {
            s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try { s.Connect(new UnixDomainSocketEndPoint(Sock)); return true; }
            catch { s.Dispose(); s = null; return false; }
        }

        static int Send(Dictionary<string, string> req, int timeoutSec = 30)
        {
            if (!TryConnect(out var s)) { Console.Error.WriteLine("[monodbg] no daemon running (use 'break' first)"); return 3; }
            using (s)
            {
                s.ReceiveTimeout = timeoutSec * 1000;
                s.Send(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(req) + "\n"));
                var resp = ReadLine(s);
                if (resp == null) { Console.Error.WriteLine("[monodbg] no response"); return 5; }
                var d = JsonDocument.Parse(resp).RootElement;
                bool ok = d.GetProperty("ok").GetBoolean();
                string text = d.TryGetProperty("text", out var t) ? t.GetString() : "";
                int code = d.TryGetProperty("code", out var c) ? c.GetInt32() : (ok ? 0 : 1);
                if (!string.IsNullOrEmpty(text)) (ok ? Console.Out : Console.Error).WriteLine(text);
                return code;
            }
        }

        static string ReadLine(Socket s)
        {
            var sb = new StringBuilder(); var buf = new byte[1];
            try { while (true) { int n = s.Receive(buf); if (n <= 0) break; if (buf[0] == '\n') break; sb.Append((char)buf[0]); } }
            catch (SocketException) { return sb.Length > 0 ? sb.ToString() : null; }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        static void Usage()
        {
            Console.Error.WriteLine(
                "usage: monodbg <cmd> [--host H] [--port N]\n" +
                "  break <Type.Method> [--asm N] [--wait] [--timeout S]   arm; --wait blocks until hit\n" +
                "  inspect [<expr>] [--frame N]                            no expr: list roots; expr: this | arg | this.field.sub\n" +
                "  bp | unbreak <#|Type.Method> | unbreak --all             list | remove armed breakpoints\n" +
                "  stack | continue | status | quit");
        }
    }
}
