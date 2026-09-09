namespace NeuroCamera.Models;

/// <summary>
/// Immutable snapshot of the image-correction parameters produced by
/// <see cref="NeuroCamera.Engine.CalibrationEngine"/>. Instances are handed off between the
/// calibration/video thread and consumers by reference swap only (the type itself is
/// immutable), so no external locking is required to read a consistent set of values.
/// </summary>
public sealed record CalibrationParameters
{
    /// <summary>Gray-World white balance multiplier applied to the Blue channel.</summary>
    public double AwbGainB { get; init; } = 1.0;

    /// <summary>Gray-World white balance multiplier applied to the Green channel.</summary>
    public double AwbGainG { get; init; } = 1.0;

    /// <summary>Gray-World white balance multiplier applied to the Red channel.</summary>
    public double AwbGainR { get; init; } = 1.0;

    /// <summary>Gamma exponent (input is raised to 1/Gamma). 1.0 = no change.</summary>
    public double Gamma { get; init; } = 1.0;

    /// <summary>Linear contrast multiplier (applied as new = old * Alpha + Beta).</summary>
    public double Alpha { get; init; } = 1.0;

    /// <summary>Linear brightness offset (applied as new = old * Alpha + Beta).</summary>
    public double Beta { get; init; } = 0.0;

    /// <summary>Bilateral filter pixel neighborhood diameter. 0 disables the filter.</summary>
    public int BilateralDiameter { get; init; } = 0;

    /// <summary>Bilateral filter color-space sigma.</summary>
    public double BilateralSigmaColor { get; init; } = 0.0;

    /// <summary>Bilateral filter coordinate-space sigma.</summary>
    public double BilateralSigmaSpace { get; init; } = 0.0;

    /// <summary>True once a real calibration pass (not the neutral default) has completed.</summary>
    public bool IsCalibrated { get; init; }

    /// <summary>Neutral, pass-through parameter set used before the first calibration runs.</summary>
    public static CalibrationParameters Neutral { get; } = new();
}
