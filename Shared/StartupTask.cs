// "Start with Windows": a Task Scheduler entry that opens the app in the tray at sign-in.
// A scheduled task (run level "highest") avoids an admin prompt at every login,
// which a Run-key entry would trigger because the app needs admin rights.
using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace DiscordUnblock
{
    public static class StartupTask
    {
        public const string Name = "DiscordUnblock";

        public static bool Exists()
        {
            return Run("/Query /TN \"" + Name + "\"") == 0;
        }

        public static bool Create(string exePath)
        {
            string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            string exe = SecurityElement.Escape(exePath);
            string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Description>Opens DiscordUnblock in the system tray when you sign in.</Description></RegistrationInfo>\r\n" +
"  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>\r\n" +
"  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
"    <Priority>7</Priority>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>" + exe + "</Command><Arguments>--tray</Arguments></Exec></Actions>\r\n" +
"</Task>\r\n";
            string tmp = Path.Combine(Path.GetTempPath(), "DiscordUnblock-task-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            try { return Run("/Create /TN \"" + Name + "\" /XML \"" + tmp + "\" /F") == 0; }
            finally { try { File.Delete(tmp); } catch { } }
        }

        public static void Delete()
        {
            Run("/Delete /TN \"" + Name + "\" /F");
        }

        static int Run(string args)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            }
        }
    }
}
