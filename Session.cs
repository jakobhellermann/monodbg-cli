using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Mono.Debugger.Soft;

namespace MonoDbg
{
    // The daemon: holds the single VM connection to the agent, keeps it suspended at a
    // breakpoint, and serves discrete client commands (break/inspect/stack/continue/...)
    // over a unix socket. One `gate` lock serializes all VM access + stop-state; the event
    // loop pumps events and, on a breakpoint, stores the stopped frames and does NOT resume.
    static class Session
    {
        static VirtualMachine vm;
        static readonly object gate = new object();
        const int VmTimeoutMs = 3000;

        // Every VM round-trip goes over a socket to the agent in the game; if the agent stalls
        // (wedged, scene transition, whatever) an unbounded call blocks whichever thread holds `gate`
        // forever, which then hangs every other command too. Bound each one; on timeout the client
        // gets a clear error instead of the daemon going silently unresponsive.
        static bool TryVm<T>(Func<T> f, out T result, out string error, int timeoutMs = VmTimeoutMs)
        {
            result = default;
            var t = Task.Run(f);
            if (!t.Wait(timeoutMs)) { error = $"agent unresponsive (timed out after {timeoutMs}ms)"; return false; }
            if (t.IsFaulted) { error = t.Exception!.GetBaseException().Message; return false; }
            result = t.Result;
            error = null;
            return true;
        }

        static bool TryVm(Action f, out string error, int timeoutMs = VmTimeoutMs)
        {
            var t = Task.Run(f);
            if (!t.Wait(timeoutMs)) { error = $"agent unresponsive (timed out after {timeoutMs}ms)"; return false; }
            if (t.IsFaulted) { error = t.Exception!.GetBaseException().Message; return false; }
            error = null;
            return true;
        }
        enum State { Running, Stopped, Dead }
        static State state = State.Running;
        static StackFrame[] frames;
        static string agentError;
        static string sockPath;
        // Armed method-entry breakpoints: label "NS.Type.Method(Sig)" plus the request handle,
        // so `bp` can list them with stable indices and `unbreak` can clear individual ones
        // agent-side (EventRequest.Disable() == ClearEventRequest on the agent).
        sealed class ArmedBp { public EventRequest Req; public string Label; }
        static readonly List<ArmedBp> armed = new();
        static PosixSignalRegistration sigterm, sigint;

        public static int Run(string host, int port, string sock)
        {
            sockPath = sock;
            if (File.Exists(sock)) { try { File.Delete(sock); } catch { } }
            var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            server.Bind(new UnixDomainSocketEndPoint(sock));
            server.Listen(16);
            AppDomain.CurrentDomain.ProcessExit += (_, __) => { try { File.Delete(sock); } catch { } };

            // SIGKILL can't be caught by any code -- if you `kill -9` the daemon, an armed breakpoint
            // stays live on the agent with no one left to resume it, freezing the game solid the next
            // time that method runs. SIGTERM/SIGINT (plain `kill`, Ctrl+C) CAN be caught, so detach
            // properly here to release any armed breakpoints before the process actually exits.
            void GracefulShutdown(PosixSignalContext ctx)
            {
                ctx.Cancel = true;
                if (vm != null) TryVm(() => vm.Detach(), out _, 2000);
                Shutdown(0);
            }
            sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, GracefulShutdown);
            sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, GracefulShutdown);

            // Bounded connect: a wedged agent (e.g. left over from a prior ungraceful client
            // disconnect) never completes the handshake, and an unbounded Connect() would hang here
            // forever -- before the listening socket is ever accepted from, with zero diagnostics for
            // any client. BeginConnect's task also performs the handshake read, so cancelling it on
            // timeout (closing the socket) unblocks that read too.
            var ep = new IPEndPoint(IPAddress.Parse(host), port);
            var ar = VirtualMachineManager.BeginConnect(ep, null);
            if (!((Task)ar).Wait(TimeSpan.FromSeconds(5)))
            {
                VirtualMachineManager.CancelConnection(ar);
                agentError = $"agent at {host}:{port} did not complete the handshake within 5s " +
                             "(it may be wedged from a prior ungraceful disconnect -- try restarting the game)";
            }
            else
            {
                try { vm = VirtualMachineManager.EndConnect(ar); }
                catch (Exception e) { agentError = e.Message; }
            }

            if (agentError == null && !TryVm(() => vm.EnableEvents(EventType.VMDeath, EventType.VMDisconnect), out var enErr))
                agentError = "EnableEvents: " + enErr;

            if (agentError == null)
                new Thread(EventLoop) { IsBackground = true }.Start();

            while (true)
            {
                Socket c;
                try { c = server.Accept(); } catch { break; }
                new Thread(() => Handle(c)) { IsBackground = true }.Start();
            }
            return 0;
        }

        static void EventLoop()
        {
            while (true)
            {
                EventSet es;
                try { es = vm.GetNextEventSet(); }
                catch { lock (gate) { state = State.Dead; Monitor.PulseAll(gate); } return; }
                lock (gate)
                {
                    foreach (var e in es.Events)
                    {
                        if (e is BreakpointEvent be)
                        {
                            try { frames = be.Thread.GetFrames(); } catch { frames = Array.Empty<StackFrame>(); }
                            state = State.Stopped;
                        }
                        else if (e is VMDeathEvent || e is VMDisconnectEvent) state = State.Dead;
                    }
                    Monitor.PulseAll(gate);
                    if (state == State.Running) TryVm(() => vm.Resume(), out _);
                }
            }
        }

        static void Handle(Socket c)
        {
            using (c)
            {
                var raw = ReadLine(c);
                if (raw == null) return;
                Dictionary<string, string> m;
                try { m = JsonSerializer.Deserialize<Dictionary<string, string>>(raw); }
                catch { Reply(c, false, 1, "bad request"); return; }
                string cmd = m.GetValueOrDefault("cmd", "");

                if (agentError != null && cmd != "quit")
                {
                    Reply(c, false, 3, "[monodbg] agent connect failed: " + agentError);
                    Shutdown(3);
                    return;
                }
                switch (cmd)
                {
                    case "break": DoBreak(c, m); break;
                    case "breakpoints": DoBreakpoints(c); break;
                    case "unbreak": DoUnbreak(c, m); break;
                    case "inspect": DoInspect(c, m); break;
                    case "stack": lock (gate) Reply(c, state == State.Stopped, state == State.Stopped ? 0 : 2, state == State.Stopped ? FormatStack() : "[monodbg] not stopped (" + state + ")"); break;
                    case "continue": DoContinue(c); break;
                    case "status": DoStatus(c); break;
                    case "quit": Reply(c, true, 0, "[monodbg] detaching."); if (vm != null) TryVm(() => vm.Detach(), out _); Shutdown(0); break;
                    default: Reply(c, false, 64, "unknown cmd '" + cmd + "'"); break;
                }
            }
        }

        static void DoBreak(Socket c, Dictionary<string, string> m)
        {
            string target = m.GetValueOrDefault("target", "");
            string asm = m.GetValueOrDefault("asm", null);
            bool wait = m.GetValueOrDefault("wait", "0") == "1";
            int timeout = int.Parse(m.GetValueOrDefault("timeout", "120"));
            int dot = target.LastIndexOf('.');
            string tn = dot < 0 ? target : target.Substring(0, dot);
            string mn = dot < 0 ? "" : target.Substring(dot + 1);

            string msg; ArmResult r;
            lock (gate) r = Arm(tn, mn, asm, out msg);
            if (r != ArmResult.Armed)
            {
                int code = r switch { ArmResult.MethodMissing => 65, ArmResult.AgentUnresponsive => 67, _ => 66 };
                Reply(c, false, code, msg);
                return;
            }
            if (!wait) { Reply(c, true, 0, msg); return; }

            lock (gate)
            {
                long deadline = Environment.TickCount64 + timeout * 1000L;
                while (state == State.Running)
                {
                    long rem = deadline - Environment.TickCount64;
                    if (rem <= 0) break;
                    Monitor.Wait(gate, (int)rem);
                }
                if (state == State.Stopped) Reply(c, true, 0, msg + "\n" + FormatStack());
                else if (state == State.Dead) Reply(c, false, 4, msg + "\n[monodbg] VM dead");
                else Reply(c, false, 2, msg + "\n[monodbg] timeout waiting for hit");
            }
        }

        static void DoBreakpoints(Socket c)
        {
            lock (gate)
            {
                if (armed.Count == 0) { Reply(c, true, 0, "[monodbg] no breakpoints armed"); return; }
                var sb = new StringBuilder();
                sb.Append("[monodbg] ").Append(armed.Count).Append(" armed:");
                for (int i = 0; i < armed.Count; i++)
                    sb.Append("\n  ").Append(i).Append(": ").Append(armed[i].Label);
                sb.Append("\n[monodbg] remove with: unbreak <#> | unbreak Type.Method | unbreak --all");
                Reply(c, true, 0, sb.ToString());
            }
        }

        static void DoUnbreak(Socket c, Dictionary<string, string> m)
        {
            string target = m.GetValueOrDefault("target", "");
            bool all = m.GetValueOrDefault("all", "0") == "1";
            lock (gate)
            {
                if (armed.Count == 0) { Reply(c, false, 1, "[monodbg] no breakpoints armed"); return; }
                List<ArmedBp> picks;
                if (all) picks = armed.ToList();
                else if (target.Length == 0) { Reply(c, false, 64, "[monodbg] usage: unbreak <#|Type.Method> | --all"); return; }
                else if (int.TryParse(target, out int idx))
                {
                    if (idx < 0 || idx >= armed.Count) { Reply(c, false, 1, "[monodbg] no breakpoint #" + idx + " (see 'monodbg bp')"); return; }
                    picks = new List<ArmedBp> { armed[idx] };
                }
                else
                {
                    // 'NS.Type.Method' prefix matches ignoring the label's parameter signature
                    // (removes all overloads at once); otherwise fall back to substring
                    picks = armed.Where(b => b.Label == target || b.Label.StartsWith(target + "(")).ToList();
                    if (picks.Count == 0) picks = armed.Where(b => b.Label.Contains(target)).ToList();
                    if (picks.Count == 0) { Reply(c, false, 1, "[monodbg] no armed breakpoint matches '" + target + "' (see 'monodbg bp')"); return; }
                }
                var removed = new List<string>(); var failed = new List<string>();
                foreach (var p in picks)
                {
                    // conservative on failure: a request whose Clear round-trip failed stays
                    // listed (retry or 'quit' cleans up) rather than lingering armed but unlisted
                    if (TryVm(() => p.Req.Disable(), out var err)) { armed.Remove(p); removed.Add(p.Label); }
                    else failed.Add(p.Label + " (" + err + ")");
                }
                string text = "[monodbg] removed: " + string.Join(", ", removed);
                if (failed.Count > 0) text += "\n[monodbg] failed: " + string.Join("; ", failed);
                if (removed.Count == 0) { Reply(c, false, 5, text); return; }
                Reply(c, true, failed.Count == 0 ? 0 : 5, text);
            }
        }

        static void DoContinue(Socket c)
        {
            lock (gate)
            {
                if (state != State.Stopped) { Reply(c, false, 2, "[monodbg] not stopped (" + state + ")"); return; }
                frames = null; state = State.Running;
                if (!TryVm(() => vm.Resume(), out var err)) { Reply(c, false, 5, "[monodbg] resume failed: " + err); return; }
                Reply(c, true, 0, "[monodbg] resumed.");
            }
        }

        static void DoStatus(Socket c)
        {
            lock (gate)
            {
                var sb = new StringBuilder();
                sb.Append("[monodbg] state=").Append(state);
                sb.Append(" armed=").Append(armed.Count);
                if (armed.Count > 0) sb.Append(" (list: monodbg bp)");
                if (state == State.Stopped && frames?.Length > 0)
                    sb.Append(" at ").Append(FrameLabel(frames[0]));
                Reply(c, true, 0, sb.ToString());
            }
        }

        static void DoInspect(Socket c, Dictionary<string, string> m)
        {
            string expr = m.GetValueOrDefault("expr", "");
            int frameIdx = int.Parse(m.GetValueOrDefault("frame", "0"));
            lock (gate)
            {
                if (state != State.Stopped) { Reply(c, false, 2, "[monodbg] not stopped (" + state + ")"); return; }
                if (frames == null || frameIdx < 0 || frameIdx >= frames.Length) { Reply(c, false, 1, "[monodbg] no frame " + frameIdx); return; }
                if (string.IsNullOrEmpty(expr))
                {
                    if (!TryVm(() => ListRoots(frames[frameIdx], frameIdx), out var listing, out var err))
                    { Reply(c, false, 1, "[monodbg] " + err + " (listing frame)"); return; }
                    Reply(c, true, 0, listing);
                    return;
                }
                try { Reply(c, true, 0, Eval(frames[frameIdx], expr)); }
                catch (Exception e) { Reply(c, false, 1, "[monodbg] inspect error: " + e.Message); }
            }
        }

        // `inspect` with no expression: list the roots Eval() accepts in this frame -- `this`, the
        // method's arguments, and locals in scope. Args come from the method mirror (always
        // available); locals need the assembly's PDBs, so degrade to a note when absent. Each
        // piece is guarded individually: a wedged/partial piece must not hide the rest.
        static string ListRoots(StackFrame f, int idx)
        {
            var sb = new StringBuilder();
            sb.Append("[monodbg] frame #").Append(idx).Append(": ").Append(FrameLabel(f));

            // static frames: the agent returns a null-valued Value for `this` (not a null reference),
            // so detect it via the formatted result
            string thisLine;
            try { var t = f.GetThis(); string fmt = t != null ? Fmt(t) : null; thisLine = fmt == null || fmt == "null" ? "(static method -- no this)" : fmt; }
            catch { thisLine = "<unavailable>"; }
            sb.Append("\n  this   : ").Append(thisLine);

            sb.Append("\n  args   : ");
            try
            {
                var ps = f.Method?.GetParameters() ?? Array.Empty<ParameterInfoMirror>();
                sb.Append(ps.Length == 0 ? "(none)" : string.Join(", ", ps.Select(p => p.Name + " (" + SafeTypeName(p.ParameterType) + ")")));
            }
            catch { sb.Append("<unavailable>"); }

            sb.Append("\n  locals : ");
            try
            {
                var ls = f.GetVisibleVariables().Where(l => !l.IsArg).ToList();
                sb.Append(ls.Count == 0 ? "(none in scope)" : string.Join(", ", ls.Select(l => l.Name + " (" + SafeTypeName(l.Type) + ")")));
            }
            catch { sb.Append("(unavailable -- no PDBs/debug info; only this/args)"); }

            return sb.ToString();
        }

        static string SafeTypeName(TypeMirror t) { try { return t?.Name ?? "?"; } catch { return "?"; } }

        // ---- breakpoint arming ----
        enum ArmResult { Armed, TypeMissing, MethodMissing, Ambiguous, AgentUnresponsive }

        static ArmResult Arm(string typeName, string methodName, string asm, out string msg)
        {
            if (!TryVm(() => vm.GetTypes(typeName, false).ToList(), out List<TypeMirror> matches, out var err))
            { msg = "[monodbg] " + err + " (resolving type '" + typeName + "')"; return ArmResult.AgentUnresponsive; }
            if (matches.Count == 0) { msg = "[monodbg] type '" + typeName + "' not loaded."; return ArmResult.TypeMissing; }
            if (asm != null) matches = matches.Where(t => AsmName(t) == asm).ToList();
            if (matches.Count == 0) { msg = "[monodbg] type '" + typeName + "' not in assembly '" + asm + "'."; return ArmResult.TypeMissing; }
            if (matches.Count > 1)
            {
                msg = "[monodbg] type '" + typeName + "' ambiguous across: " + string.Join(", ", matches.Select(AsmName).Distinct()) + " -- pass --asm NAME.";
                return ArmResult.Ambiguous;
            }
            var type = matches[0];
            if (!TryVm(() => type.GetMethods().Where(x => x.Name == methodName).ToList(), out var methods, out err))
            { msg = "[monodbg] " + err + " (resolving methods on " + type.FullName + ")"; return ArmResult.AgentUnresponsive; }
            if (methods.Count == 0)
            {
                if (!TryVm(() => type.GetMethods().Select(x => x.Name).Distinct()
                        .Where(n => n.IndexOf(methodName, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(x => x).ToList(),
                    out var near, out err))
                { msg = "[monodbg] no method '" + methodName + "' on " + type.FullName + ", and " + err + " listing closest matches."; return ArmResult.AgentUnresponsive; }
                msg = "[monodbg] no method '" + methodName + "' on " + type.FullName + ". closest: " + string.Join(", ", near);
                return ArmResult.MethodMissing;
            }
            var newly = new List<string>(); int skipped = 0;
            if (!TryVm(() =>
            {
                foreach (var mm in methods)
                {
                    string label = type.FullName + "." + mm.Name + "(" + string.Join(", ", mm.GetParameters().Select(p => p.ParameterType != null ? p.ParameterType.Name : "?")) + ")";
                    if (armed.Any(b => b.Label == label)) { skipped++; continue; } // re-arm: no-op, not a second request
                    var req = vm.CreateBreakpointRequest(mm, 0);
                    req.Enable();
                    armed.Add(new ArmedBp { Req = req, Label = label });
                    newly.Add(label);
                }
            }, out err))
            { msg = "[monodbg] " + err + " (arming breakpoint)"; return ArmResult.AgentUnresponsive; }
            msg = newly.Count > 0
                ? "[monodbg] armed: " + string.Join(", ", newly) + " in " + AsmName(type) + (skipped > 0 ? " (" + skipped + " already armed)" : "")
                : "[monodbg] " + type.FullName + "." + methodName + ": already armed";
            return ArmResult.Armed;
        }

        // ---- field-only expression eval ----
        static string Eval(StackFrame f, string expr)
        {
            var segs = expr.Split('.');
            Value cur = ResolveRoot(f, segs[0]);
            for (int i = 1; i < segs.Length; i++) cur = FieldOf(cur, segs[i]);

            var sb = new StringBuilder();
            sb.Append(expr).Append(" = ").Append(Fmt(cur));
            foreach (var (name, val) in Expand(cur)) sb.Append("\n  .").Append(name).Append(" = ").Append(Fmt(val));
            return sb.ToString();
        }

        static Value ResolveRoot(StackFrame f, string name)
        {
            if (name == "this")
            {
                var t = f.GetThis();
                if (t == null) throw new Exception("frame has no 'this' (static method?)");
                return t;
            }
            var p = f.Method.GetParameters().FirstOrDefault(x => x.Name == name);
            if (p != null) return f.GetValue(p);
            var lv = f.GetVisibleVariableByName(name);
            if (lv != null) return f.GetValue(lv);
            throw new Exception("no root '" + name + "' (not this/arg/local; locals need PDB)");
        }

        static Value FieldOf(Value v, string name)
        {
            if (v is ObjectMirror om)
            {
                var fld = InstanceAndStaticFields(om.Type).FirstOrDefault(x => x.Name == name)
                          ?? throw new Exception("no field '" + name + "' on " + om.Type.FullName);
                return om.GetValue(fld);
            }
            if (v is StructMirror sm)
            {
                var flds = InstanceFields(sm.Type);
                int idx = flds.FindIndex(x => x.Name == name);
                if (idx < 0 || idx >= sm.Fields.Length) throw new Exception("no field '" + name + "' on struct " + sm.Type.FullName);
                return sm.Fields[idx];
            }
            throw new Exception("cannot read field '" + name + "' on " + Fmt(v));
        }

        static IEnumerable<(string, Value)> Expand(Value v)
        {
            if (v is ObjectMirror om)
                foreach (var f in InstanceFields(om.Type))
                { Value val; try { val = om.GetValue(f); } catch { val = null; } yield return (f.Name, val); }
            else if (v is StructMirror sm)
            {
                var flds = InstanceFields(sm.Type);
                for (int i = 0; i < flds.Count && i < sm.Fields.Length; i++) yield return (flds[i].Name, sm.Fields[i]);
            }
        }

        static List<FieldInfoMirror> InstanceFields(TypeMirror t)
        {
            var seen = new HashSet<string>(); var acc = new List<FieldInfoMirror>();
            for (var cur = t; cur != null; cur = SafeBase(cur))
                foreach (var f in cur.GetFields())
                    if ((f.Attributes & FieldAttributes.Static) == 0 && seen.Add(f.Name)) acc.Add(f);
            return acc;
        }

        static List<FieldInfoMirror> InstanceAndStaticFields(TypeMirror t)
        {
            var seen = new HashSet<string>(); var acc = new List<FieldInfoMirror>();
            for (var cur = t; cur != null; cur = SafeBase(cur))
                foreach (var f in cur.GetFields())
                    if (seen.Add(f.Name)) acc.Add(f);
            return acc;
        }

        static TypeMirror SafeBase(TypeMirror t) { try { return t.BaseType; } catch { return null; } }

        // ---- formatting ----
        static string FormatStack()
        {
            if (frames == null) return "[monodbg] no frames";
            var sb = new StringBuilder();
            for (int i = 0; i < frames.Length; i++) sb.Append(i == 0 ? "" : "\n").Append("  #").Append(i.ToString().PadRight(2)).Append(' ').Append(FrameLabel(frames[i]));
            return sb.ToString();
        }

        static string FrameLabel(StackFrame f)
        {
            var m = f.Method;
            string owner = m?.DeclaringType != null ? m.DeclaringType.FullName : "?";
            string sig = m == null ? "?" : m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType != null ? p.ParameterType.Name : "?")) + ")";
            string a = m?.DeclaringType != null ? AsmName(m.DeclaringType) : "?";
            return owner + "." + sig + "  [IL_" + f.ILOffset.ToString("x4") + "]  <" + a + ">";
        }

        static string Fmt(Value v)
        {
            try
            {
                if (v == null) return "null";
                if (v is PrimitiveValue pv) return pv.Value == null ? "null" : pv.Value.ToString();
                if (v is StringMirror sm) return "\"" + sm.Value + "\"";
                if (v is EnumMirror em) return "enum " + em.Type.Name;
                if (v is StructMirror st) return "struct " + st.Type.Name;
                if (v is ObjectMirror om) return om.Type.FullName + "#" + om.Address;
                return v.ToString();
            }
            catch (Exception e) { return "<err: " + e.Message + ">"; }
        }

        static string AsmName(TypeMirror t) { try { return t.Assembly.GetName().Name; } catch { return "?"; } }

        // ---- ipc ----
        static void Reply(Socket c, bool ok, int code, string text)
        {
            var o = new Dictionary<string, object> { ["ok"] = ok, ["code"] = code, ["text"] = text };
            try { c.Send(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o) + "\n")); } catch { }
        }

        static string ReadLine(Socket c)
        {
            var sb = new StringBuilder(); var buf = new byte[1];
            try { while (true) { int n = c.Receive(buf); if (n <= 0) break; if (buf[0] == '\n') break; sb.Append((char)buf[0]); } }
            catch (SocketException) { return sb.Length > 0 ? sb.ToString() : null; }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        static void Shutdown(int code) { try { File.Delete(sockPath); } catch { } Environment.Exit(code); }
    }
}
