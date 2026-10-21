namespace DeskrawlHubClient;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleton = new Mutex(initiallyOwned: true, "Local\\DeskrawlHubClient.Singleton", out bool created);
        if (!created)
        {
            MessageBox.Show("控制台已经打开。", "桌破控制台", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
