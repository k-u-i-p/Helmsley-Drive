namespace HelmsleyDrive.App;

/// <summary>
/// How a second launch reaches the first one's window.
///
/// The tray took something away that the singleton lock used to be able to rely on: that a running
/// instance was a visible one. Now the Start Menu shortcut somebody clicks to get the window back
/// is a second launch, and the honest answer to it is not a message box saying the app is already
/// running — it is the window. So the second instance sets a named event and leaves; the first is
/// waiting on it, and shows itself.
///
/// Keyed on the root, like the lock it shadows, so a probe instance on a second root does not
/// summon the real drive's window. Local, so it cannot reach across sessions on a shared machine.
/// </summary>
static class ShowRequest
{
    static string Name(string rootKey) => $@"Local\HelmsleyDrive.App.Show.{rootKey}";

    /// <summary>Answers show requests until disposed, on a thread of its own.</summary>
    public static IDisposable Listen(string rootKey, Action show)
    {
        var asked = new EventWaitHandle(false, EventResetMode.AutoReset, Name(rootKey));
        var stop = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            // Index 0 is a request, index 1 is Dispose. Anything else ends the loop too, which is
            // the right answer to a handle that has gone wrong: a background thread spinning on a
            // broken wait would be worse than a window that stops answering the Start Menu.
            while (WaitHandle.WaitAny(new WaitHandle[] { asked, stop.Token.WaitHandle }) == 0)
            {
                try { show(); }
                catch (Exception e) { Console.Error.WriteLine($"a show request failed: {e.Message}"); }
            }
        })
        {
            IsBackground = true,
            Name = "show-requests",
        };
        thread.Start();
        return new Listener(stop, asked, thread);
    }

    /// <summary>
    /// Asks whoever holds this root to show its window. False when nobody is listening — an
    /// instance still starting up, or a maintenance flag holding the lock — which is the caller's
    /// cue to say so out loud instead.
    /// </summary>
    public static bool Send(string rootKey)
    {
        if (!EventWaitHandle.TryOpenExisting(Name(rootKey), out var asked)) return false;
        using (asked) { return asked.Set(); }
    }

    sealed class Listener(CancellationTokenSource stop, EventWaitHandle asked, Thread thread) : IDisposable
    {
        public void Dispose()
        {
            stop.Cancel();
            // Waited for, and not merely signalled: both handles below are ones that thread is
            // sitting inside a WaitAny on, and disposing a handle out from under a waiter is how
            // a clean exit turns into an exception on the way out. A second is generous for a
            // thread whose only remaining instruction is to notice and return; past that it is a
            // background thread and the process is leaving anyway.
            thread.Join(TimeSpan.FromSeconds(1));
            asked.Dispose();
            stop.Dispose();
        }
    }
}
