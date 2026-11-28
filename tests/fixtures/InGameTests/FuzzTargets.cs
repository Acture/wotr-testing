using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using WotR.Testing.Fuzzing;
using WotR.Testing.InGame;

namespace InGameTests;

/// <summary>Failure scenarios chosen by the integration tests; a file, because the game does not see their environment.</summary>
public static class Scenario
{
    public static bool Is(string name)
    {
        var file = Path.Combine(Path.GetDirectoryName(typeof(Scenario).Assembly.Location), "wotr-test-scenario.json");
        return File.Exists(file) && File.ReadAllText(file).Contains("\"" + name + "\"");
    }
}

public interface ICounter
{
    void Add(int value);
    int Total();
}

public sealed class Counter : ICounter
{
    private long total;
    public void Add(int value) => total += value;
    public int Total() => unchecked((int)total);
}

/// <summary>Every call waits a frame on Unity's main thread; a blocking fuzzer would never let that frame come.</summary>
public interface IFrameAccount : IDisposable
{
    Task Deposit(byte amount);
    Task<int> Withdraw(byte amount);
}

public sealed class FrameAccount : IFrameAccount
{
    private readonly bool defect = Scenario.Is("fuzz-defect");
    private readonly GameObject owner;
    private int balance;

    public FrameAccount()
    {
        InGameRuntime.Dispatcher.RequireMainThread("FrameAccount");
        owner = new GameObject("WotR.Testing.FrameAccount");
    }

    public async Task Deposit(byte amount)
    {
        var frame = Time.frameCount;
        await InGameRuntime.Dispatcher.NextFrame();
        InGameRuntime.Dispatcher.RequireMainThread("Deposit");
        if (Time.frameCount <= frame) throw new InvalidOperationException("No frame passed.");
        balance += amount;
    }

    public async Task<int> Withdraw(byte amount)
    {
        await InGameRuntime.Dispatcher.NextFrame();
        InGameRuntime.Dispatcher.RequireMainThread("Withdraw");
        if (amount > balance) {
            if (defect) throw new InvalidOperationException("Overdrawn by " + (amount - balance));
            amount = (byte)balance;
        }
        balance -= amount;
        return balance;
    }

    public void Dispose()
    {
        InGameRuntime.Dispatcher.RequireMainThread("Dispose");
        UnityEngine.Object.Destroy(owner);
    }
}

public static class Registrations
{
    [WotrFuzz(Modes = WotrTestModes.Both)]
    public static ICounter CreateCounter() => new Counter();

    [WotrFuzz(Modes = WotrTestModes.InGame, OptionsProvider = nameof(AccountOptions))]
    public static IFrameAccount CreateAccount() => new FrameAccount();

    public static FuzzOptions AccountOptions() => new FuzzOptions { Cases = 20, MaxCalls = 6, MaxShrinks = 200 };
}
