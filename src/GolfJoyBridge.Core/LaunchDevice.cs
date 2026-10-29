namespace GolfJoyBridge;

/// <summary>What the rest of the program needs from the launch monitor. The real one wraps GRS.dll.</summary>
public interface ILaunchDevice : IDisposable
{
    event Action<SpicaBall>? Ball;
    event Action<SpicaClub>? Club;
    /// <summary>Ball state code from the unit: 1 = ball placed, 0 = swing in progress, 2 = shot complete (my reading of the logs).</summary>
    event Action<int>? BallState;
    event Action<RawImage>? Image;
    /// <summary>true = the unit is sending heartbeats (connected); false = heartbeats stopped (cable pulled, unit off).</summary>
    event Action<bool>? Connection;

    bool Start(out string error);
    void Stop();

    /// <summary>-1 while the unit is still connecting, otherwise 0 = left, 1 = right.</summary>
    int GetHand();
    void SetHand(bool right);
    void SetClub(int clubNumber);

    /// <summary>One line describing the unit (firmware, battery) for the log.</summary>
    string Describe();
}
