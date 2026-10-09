using System.Numerics;

namespace Illusion.Formats.Prefab;

/// <summary>
/// How a car is assembled, read off its PREFAB. Every reference here is the FNV64 hash of a FRAME name —
/// a bone, a dummy or a point of the car's own model — so this is the layer that says which bone is a door,
/// where its handle and lock are, which frames are the axles and wheels, and where a player may climb on.
/// <para>
/// A read-only view over the decoded model; the fields the game reads and nothing invented on top.
/// </para>
/// </summary>
public sealed class CarPrefab
{
    private readonly Native.Model.PrefabCarInitW _car;

    internal CarPrefab(Native.Model.PrefabCarInitW car) => _car = car;

    private Native.Model.PrefabDeformationInitW? Deformation =>
        _car.Deformation.Count > 0 ? _car.Deformation[0] : null;

    private Native.Model.PrefabOtherInitW? Other => _car.Other.Count > 0 ? _car.Other[0] : null;

    /// <summary>The frame the whole body hangs off, and the bone the rig is scaled by.</summary>
    public ulong RootFrame => Deformation?.RootFrameName ?? 0;

    public ulong ScaleBone => Deformation?.ScaleBoneFrameName ?? 0;

    public ulong BodyFrame => Other?.VehicleBodyName ?? 0;

    public ulong RestBone => Other?.RestBoneName ?? 0;

    /// <summary>Frames the lights are modelled by — headlight, backlight, toplight — and the snow rest.</summary>
    public ulong HeadlightModel => Other?.HeadlightModelName ?? 0;

    public ulong BacklightModel => Other?.BacklightModelName ?? 0;

    public ulong ToplightModel => Other?.ToplightModelName ?? 0;

    public ulong SnowRest => Other?.SnowRestName ?? 0;

    public IReadOnlyList<ulong> DrivingWheels => Other?.DrivingWheels ?? [];

    public IReadOnlyList<ulong> FuelTanks => Other?.FuelTanks ?? [];

    public IReadOnlyList<ulong> ExhaustEmitters => Other?.ExhaustEmitters ?? [];

    public IReadOnlyList<ulong> Wipers => _car.WipersFrameName;

    /// <summary>
    /// The car's lights as the game reads them: the frame (a bone) whose piece of the body is lit, two numbers
    /// that say what kind of light it is and which group it switches with, how strongly the piece glows and
    /// how it pulses, the bones that must be whole for it to work, and the model of the light it casts.
    /// The meaning of the unnamed numbers is not established; they are given as stored.
    /// </summary>
    public IReadOnlyList<Light> Lights =>
        [.. _car.ShaderEffects.SelectMany(e => e.Lights).Select(l => new Light(l.FrameName, l.Unk1, l.Unk2, l.Unk3, l.Unk4,
            l.EmissivePower, l.EmissiveMiddle, l.EmissiveSpeed0, l.EmissiveSpeed1, [.. l.CheckBoneName], l.LightModelHash,
            l.ParticleBreakId, l.Unk12))];

    /// <summary>Where the car's light sources stand, in the model's space: a place and three axes each.</summary>
    public IReadOnlyList<(Vector3 Translation, Vector3 Row0, Vector3 Row1, Vector3 Row2)> LightMatrices =>
        [.. (Other?.LightMatrices ?? []).Select(m => (m.Translation, m.Row0, m.Row1, m.Row2))];

    /// <summary>How many deformable parts the body is split into (the damage model).</summary>
    public int DeformPartCount => Deformation?.DeformParts.Count ?? 0;

    /// <summary>The seats, in file order: which door frame gets in, and where the occupant sits.</summary>
    public IReadOnlyList<Seat> Seats =>
        [.. _car.Seats.Select(s => new Seat(s.DoorIndexFrameName, s.FrameName, s.Position, s.SeatType, s.SeatIndex))];

    /// <summary>Per door: the frame, and where its handle and lock are in the model.</summary>
    public IReadOnlyList<DoorPoints> Doors =>
        [.. _car.DoorPoints.Select(d => new DoorPoints(d.DoorFrameName, d.HandlePos, d.LockPos))];

    /// <summary>The boxes a player may climb on, each tied to a bone and a dummy frame.</summary>
    public IReadOnlyList<ClimbBox> ClimbBoxes =>
        [.. _car.ClimbBoxes.Select(c => new ClimbBox(c.BoneFrameName, c.DummyFrameName, c.BoxMin, c.BoxMax))];

    /// <summary>The windows, each with its frame and whether it can be rolled down.</summary>
    public IReadOnlyList<Window> Windows =>
        [.. (Other?.WindowData ?? []).Select(win => new Window(win.WindowFrameName, win.Depth, win.IsOpenable != 0))];

    /// <summary>The axles. The file stores PAIRS — this is the expanded list, two per stored pair.</summary>
    public IReadOnlyList<Axle> Axles =>
        [.. _car.Axles.Select(a => new Axle(a.AxleName, a.BrakeDrumName, a.RotWingName, a.AxleType,
            a.Wheel.BrakeDrumRadius, a.Wheel.BrakeDrumMass, a.Wheel.AxleMass))];

    /// <summary>One light of the car (see <see cref="Lights"/>).</summary>
    public sealed record Light(ulong Frame, int Unk1, int Unk2, uint Unk3, uint Unk4, float EmissivePower, float EmissiveMiddle,
        float EmissiveSpeed0, float EmissiveSpeed1, IReadOnlyList<ulong> CheckBones, ulong LightModel, uint ParticleBreakId, uint Unk12);

    /// <summary>Every frame-name hash this prefab references, for resolving against the archive's frames.</summary>
    public IEnumerable<ulong> AllFrameReferences
    {
        get
        {
            foreach (ulong hash in new[] { RootFrame, ScaleBone, BodyFrame, RestBone, HeadlightModel,
                                           BacklightModel, ToplightModel, SnowRest })
            {
                if (hash != 0) yield return hash;
            }
            foreach (ulong hash in DrivingWheels.Concat(FuelTanks).Concat(ExhaustEmitters).Concat(Wipers))
            {
                if (hash != 0) yield return hash;
            }
            foreach (Seat s in Seats)
            {
                if (s.Frame != 0) yield return s.Frame;
                if (s.DoorFrame != 0) yield return s.DoorFrame;
            }
            foreach (DoorPoints d in Doors)
            {
                if (d.Frame != 0) yield return d.Frame;
            }
            foreach (ClimbBox c in ClimbBoxes)
            {
                if (c.Bone != 0) yield return c.Bone;
                if (c.Dummy != 0) yield return c.Dummy;
            }
            foreach (Window win in Windows)
            {
                if (win.Frame != 0) yield return win.Frame;
            }
            foreach (Axle a in Axles)
            {
                if (a.Frame != 0) yield return a.Frame;
                if (a.BrakeDrum != 0) yield return a.BrakeDrum;
            }
        }
    }

    public readonly record struct Seat(ulong DoorFrame, ulong Frame, Vector3 Position, uint Type, uint Index);

    public readonly record struct DoorPoints(ulong Frame, Vector3 HandlePosition, Vector3 LockPosition);

    public readonly record struct ClimbBox(ulong Bone, ulong Dummy, Vector3 Min, Vector3 Max);

    public readonly record struct Window(ulong Frame, float Depth, bool IsOpenable);

    public readonly record struct Axle(ulong Frame, ulong BrakeDrum, ulong RotWing, uint Type,
        float BrakeDrumRadius, float BrakeDrumMass, float AxleMass);
}
