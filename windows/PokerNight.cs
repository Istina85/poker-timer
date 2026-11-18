// Poker Night — запуск покерного таймера (index.html рядом с программой) без установки.
// Открывает страницу в окне Microsoft Edge (или Google Chrome) в режиме приложения.
// Профиль браузера хранится в папке data рядом с программой — всё переносится вместе с флешкой.
// Прав администратора и интернета не нужно.
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

static class PokerNight
{
    [STAThread]
    static void Main()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string index = Path.Combine(dir, "index.html");
        if (!File.Exists(index))
        {
            MessageBox.Show("Не найден файл index.html рядом с программой:\n" + dir,
                "Poker Night", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string browser = FindBrowser();
        if (browser == null)
        {
            MessageBox.Show("Не найден Microsoft Edge или Google Chrome.\nУстановите один из них или откройте index.html в любом браузере.",
                "Poker Night", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string data = Path.Combine(dir, "data");
        Directory.CreateDirectory(data);

        var psi = new ProcessStartInfo(browser,
            "--app=\"" + new Uri(index).AbsoluteUri + "\" --user-data-dir=\"" + data + "\" --no-first-run --window-size=1280,800");
        psi.UseShellExecute = false;
        Process.Start(psi);
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
