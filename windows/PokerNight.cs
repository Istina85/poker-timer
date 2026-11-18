// Poker Night — покерный таймер для Windows без установки.
// Программа поднимает локальный сервер только на 127.0.0.1: отдаёт index.html и хранит настройки
// в settings.json рядом с exe. Окно открывается в Microsoft Edge (или Google Chrome) в режиме приложения.
// Профиль браузера временный, в %LOCALAPPDATA%\PokerNight: удаляется после закрытия окна.
// На флешке остаются только exe, index.html, Read_me.txt и settings.json. Прав администратора не нужно.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class PokerNight
{
    const int PreferredPort = 47651;          // если занят — берётся свободный порт
    const int MaxBodyBytes = 1024 * 1024;     // настройки не больше 1 МБ

    static string dir, indexPath, settingsPath, localDir;
    static readonly object gate = new object();
    static string pending;                    // последние настройки, пришедшие со страницы
    static bool dirty;                        // ещё не записаны в settings.json

    [STAThread]
    static void Main()
    {
        dir = AppDomain.CurrentDomain.BaseDirectory;
        indexPath = Path.Combine(dir, "index.html");
        settingsPath = Path.Combine(dir, "settings.json");
        localDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PokerNight");

        if (!File.Exists(indexPath))
        {
            Show("Не найден index.html рядом с программой:\n" + dir, MessageBoxIcon.Error);
            return;
        }
        string browser = FindBrowser();
        if (browser == null)
        {
            Show("Не найден Microsoft Edge или Google Chrome.\nУстановите один из них.", MessageBoxIcon.Warning);
            return;
        }

        bool first;
        using (var single = new Mutex(true, @"Local\PokerNight-single", out first))
        {
            if (!first)
            {
                Show("Таймер уже открыт.", MessageBoxIcon.Information);
                return;
            }
            CleanOldProfiles();

            int port;
            TcpListener listener = StartListener(out port);
            var serverThread = new Thread(() => AcceptLoop(listener)) { IsBackground = true };
            serverThread.Start();
            var flushTimer = new System.Threading.Timer(_ => Flush(), null, 5000, 5000);

            string profile = Path.Combine(localDir, "profile-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(profile);
            // Файл «First Run» в профиле отключает экран первого запуска Edge/Chrome (приветствие, импорт, вход)
            File.WriteAllText(Path.Combine(profile, "First Run"), "");
            var psi = new ProcessStartInfo(browser,
                "--app=\"http://127.0.0.1:" + port + "/\" --user-data-dir=\"" + profile + "\""
                + " --no-first-run --no-default-browser-check --disable-sync --hide-crash-restore-bubble"
                + " --disable-features=msEdgeWelcomePage,Translate --window-size=1280,800");
            psi.UseShellExecute = false;
            using (Process p = Process.Start(psi))
            {
                // ждём, пока пользователь закроет окно (фоновые процессы Edge не считаются)
                WaitWindowClosed(profile);
            }
            // Edge оставляет фоновые процессы своего профиля — завершаем только их (по пути профиля)
            KillProfileProcesses(profile);

            flushTimer.Dispose();
            listener.Stop();
            Flush();
            DeleteDir(profile);
        }
    }

    static void Show(string text, MessageBoxIcon icon)
    {
        MessageBox.Show(text, "Poker Night", MessageBoxButtons.OK, icon);
    }

    // Порт 47651; если он занят, система выберет свободный
    static TcpListener StartListener(out int port)
    {
        var l = new TcpListener(IPAddress.Loopback, PreferredPort);
        try
        {
            l.Start();
            port = PreferredPort;
            return l;
        }
        catch (SocketException)
        {
            l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            port = ((IPEndPoint)l.LocalEndpoint).Port;
            return l;
        }
    }

    static void AcceptLoop(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Handle(client); }
                catch { }
                finally { client.Close(); }
            });
        }
    }

    static void Handle(TcpClient client)
    {
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 5000;
        NetworkStream ns = client.GetStream();

        string head = ReadHead(ns);
        if (head == null) return;
        string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
        string[] req = lines[0].Split(' ');
        if (req.Length < 2) return;
        string method = req[0], path = req[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int c = lines[i].IndexOf(':');
            if (c > 0) headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
        }

        string host;
        if (!headers.TryGetValue("Host", out host) || !host.StartsWith("127.0.0.1", StringComparison.Ordinal))
        {
            Send(ns, 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("forbidden"));
            return;
        }

        int len = 0;
        string cl;
        if (headers.TryGetValue("Content-Length", out cl)) int.TryParse(cl, out len);
        if (len < 0 || len > MaxBodyBytes)
        {
            Send(ns, 413, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("too large"));
            return;
        }
        byte[] body = ReadExact(ns, len);
        if (body == null) return;

        if ((method == "GET" || method == "HEAD") && (path == "/" || path == "/index.html"))
        {
            Send(ns, 200, "text/html; charset=utf-8", method == "HEAD" ? new byte[0] : File.ReadAllBytes(indexPath));
        }
        else if (method == "GET" && path == "/api/settings")
        {
            byte[] data = null;
            lock (gate)
            {
                if (pending != null) data = Encoding.UTF8.GetBytes(pending);
                else if (File.Exists(settingsPath)) data = File.ReadAllBytes(settingsPath);
            }
            if (data == null) Send(ns, 404, "text/plain; charset=utf-8", new byte[0]);
            else Send(ns, 200, "application/json; charset=utf-8", data);
        }
        else if ((method == "PUT" || method == "POST") && path == "/api/settings")
        {
            string s = Encoding.UTF8.GetString(body).Trim();
            if (s.Length == 0 || s[0] != '{') { Send(ns, 400, "text/plain; charset=utf-8", new byte[0]); return; }
            lock (gate)
            {
                pending = s;
                dirty = true;
            }
            Send(ns, 204, "text/plain; charset=utf-8", new byte[0]);
        }
        else
        {
            Send(ns, 404, "text/plain; charset=utf-8", new byte[0]);
        }
    }

    // Пишем на флешку не чаще раза в 5 секунд и при выходе (меньше записей на флешку)
    static void Flush()
    {
        lock (gate)
        {
            if (!dirty || pending == null) return;
            try
            {
                string tmp = settingsPath + ".tmp";
                File.WriteAllText(tmp, pending, new UTF8Encoding(false));
                File.Copy(tmp, settingsPath, true);
                File.Delete(tmp);
                dirty = false;
            }
            catch { }
        }
    }

    static string ReadHead(NetworkStream ns)
    {
        var buf = new List<byte>();
        while (buf.Count < 16384)
        {
            int b = ns.ReadByte();
            if (b < 0) return null;
            buf.Add((byte)b);
            int n = buf.Count;
            if (n >= 4 && buf[n - 4] == '\r' && buf[n - 3] == '\n' && buf[n - 2] == '\r' && buf[n - 1] == '\n')
                return Encoding.ASCII.GetString(buf.ToArray(), 0, n - 4);
        }
        return null;
    }

    static byte[] ReadExact(NetworkStream ns, int len)
    {
        var data = new byte[len];
        int got = 0;
        while (got < len)
        {
            int r = ns.Read(data, got, len - got);
            if (r <= 0) return null;
            got += r;
        }
        return data;
    }

    static void Send(NetworkStream ns, int code, string type, byte[] body)
    {
        string reason = code == 200 ? "OK" : code == 204 ? "No Content" : code == 400 ? "Bad Request"
            : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : code == 413 ? "Payload Too Large" : "Error";
        string head = "HTTP/1.1 " + code + " " + reason + "\r\n"
            + "Content-Type: " + type + "\r\n"
            + "Content-Length: " + body.Length + "\r\n"
            + "Cache-Control: no-store\r\n"
            + "Connection: close\r\n\r\n";
        byte[] h = Encoding.ASCII.GetBytes(head);
        ns.Write(h, 0, h.Length);
        if (body.Length > 0) ns.Write(body, 0, body.Length);
        ns.Flush();
    }

    // Окно считается закрытым, если у процессов браузера с этим профилем нет окон 5 секунд подряд
    static void WaitWindowClosed(string profile)
    {
        int quiet = 0;
        while (quiet < 5)
        {
            Thread.Sleep(1000);
            quiet = BrowserWindowOpen(profile) ? 0 : quiet + 1;
        }
    }

    static List<int> ProfilePids(string profile)
    {
        var pids = new List<int>();
        try
        {
            using (var s = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='msedge.exe' OR Name='chrome.exe'"))
            {
                foreach (ManagementObject o in s.Get())
                {
                    string cl = o["CommandLine"] as string;
                    if (cl != null && cl.IndexOf(profile, StringComparison.OrdinalIgnoreCase) >= 0)
                        pids.Add(Convert.ToInt32(o["ProcessId"]));
                }
            }
        }
        catch { }
        return pids;
    }

    static bool BrowserWindowOpen(string profile)
    {
        foreach (int pid in ProfilePids(profile))
        {
            try
            {
                // окно нашего приложения: заголовок страницы «Poker Night»
                Process pr = Process.GetProcessById(pid);
                if (pr.MainWindowHandle != IntPtr.Zero && pr.MainWindowTitle.IndexOf("Poker Night", StringComparison.Ordinal) >= 0) return true;
            }
            catch { }
        }
        return false;
    }

    static void KillProfileProcesses(string profile)
    {
        foreach (int pid in ProfilePids(profile))
        {
            try { Process.GetProcessById(pid).Kill(); }
            catch { }
        }
        Thread.Sleep(1000);
    }

    static void CleanOldProfiles()
    {
        try
        {
            if (!Directory.Exists(localDir)) return;
            foreach (string d in Directory.GetDirectories(localDir, "profile-*")) DeleteDir(d);
        }
        catch { }
    }

    static void DeleteDir(string d)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(d)) Directory.Delete(d, true);
                return;
            }
            catch { Thread.Sleep(1000); }
        }
    }

    static string FindBrowser()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        {
            Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(local, @"Google\Chrome\Application\chrome.exe")
        };
        foreach (string p in candidates)
            if (File.Exists(p)) return p;
        return null;
    }
}
