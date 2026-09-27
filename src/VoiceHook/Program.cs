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
            MessageBox.Show("VoiceHook is already running. Open it from the tray.", "VoiceHook");
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
