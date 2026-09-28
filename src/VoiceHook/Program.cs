namespace VoiceHook;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(
            true,
            "Local\\VoiceHook-" + Environment.UserName,
            out var created
        );
        if (!created)
        {
            try { Controls.ShowExisting().GetAwaiter().GetResult(); }
            catch { MessageBox.Show("VoiceHook is running but could not open its window. Try its tray icon.", "VoiceHook"); }
            return;
        }
        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception e)
        {
            MessageBox.Show("VoiceHook could not start: " + e.Message, "VoiceHook");
        }
    }
}
