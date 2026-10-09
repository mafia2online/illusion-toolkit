namespace Illusion.Mcp;

/// <summary>
/// What the application does for the tools that build a model's assets rather than edit a scene: materials
/// of its own, and the checks only the game would otherwise make. Kept apart from
/// <see cref="IEditorSession"/> because none of it needs an open scene - each call names its archive.
/// Every method returns null on success, else the reason nothing was done.
/// </summary>
public interface ICarWorkshop
{
    /// <summary>Copies a game material under a new name, re-pointing the named samplers at pictures of the
    /// caller's own, which are written into the archive's working copy.</summary>
    string? MaterialVariant(string source, string name, string archive, IReadOnlyDictionary<string, string> textures,
        out MaterialVariantInfo? result);

    /// <summary>Checks a car's body for what only the game shows.</summary>
    string? CarCheck(string car, string? reference, IReadOnlyDictionary<string, string> same, out CarCheckInfo? result);

    /// <summary>Writes a picture into an archive's working copy under a texture name (the archive's own copy
    /// of a texture it already carries, or a new one), or takes a texture out of it.</summary>
    string? ArchiveTexture(string archive, string texture, string? image, bool remove, out ArchiveTextureInfo? result);

    /// <summary>Takes a material the toolkit added out of the library again.</summary>
    string? MaterialDelete(string name, out string? library);

    /// <summary>A car's lights (the list in its PREFAB) and the bones of its body's rig.</summary>
    string? CarLights(string car, out CarLightsInfo? result);

    /// <summary>Writes the light of one bone of a car - changed, or added when the bone has none.</summary>
    string? CarLightSet(string car, string bone, string? kind, string? side, string? model, IReadOnlyList<string> checkBones,
        double? power, out bool added, out CarLightsInfo? result);

    /// <summary>Takes the light of a bone out of a car.</summary>
    string? CarLightRemove(string car, string bone, out CarLightsInfo? result);

    /// <summary>Adds a bone to the rig of a car's body.</summary>
    string? CarBoneAdd(string car, string name, string parent, double x, double y, double z, double reach, out CarBoneInfo? result);

    /// <summary>Gives a car the police cars' roof beacon: two bones and a light entry.</summary>
    string? CarBeacon(string car, double x, double y, double z, string checkBone, double reach, out CarBeaconInfo? result);

    /// <summary>A car's collisions, component by component, in the model's space.</summary>
    string? CarCollisions(string car, out CarCollisionsInfo? result);

    /// <summary>Gives a component of a car one more collision: a primitive shape of a size, at a place in the
    /// model's space.</summary>
    string? CarCollisionAdd(string car, string component, string role, string shape, double sizeX, double sizeY, double sizeZ,
        double x, double y, double z, out CarCollisionsInfo? result);

    /// <summary>Takes one collision off a component, by its number in <see cref="CarCollisions"/>.</summary>
    string? CarCollisionRemove(string car, string component, int index, out CarCollisionsInfo? result);

    /// <summary>Makes a car's winter twin (name_z) again from its summer working copy, with the winter
    /// materials, textures and effects the reference car's own pair shows.</summary>
    string? CarWinter(string car, string reference, out CarWinterInfo? result);

    /// <summary>The materials a car's PREFAB tells the game to colour, dirty, burn and deform.</summary>
    string? CarMaterials(string car, out CarMaterialsInfo? result);

    /// <summary>Gives a material the rows another has in those lists (null <paramref name="like"/>: takes its
    /// rows out).</summary>
    string? CarMaterialLike(string car, string material, string? like, out int changed, out CarMaterialsInfo? result);

    /// <summary>Why a car's working copy must not be written under the resource editor right now - it has the
    /// car on its stage with unsaved edits or in a Blender session - or null when it may.</summary>
    string? StageBusy(string car);

    /// <summary>Why what is on disk is not the car as the resource editor shows it - it has the car on its
    /// stage with unsaved edits - or null. What a tool that READS the working copy asks first.</summary>
    string? StageUnsaved(string car);

    /// <summary>What follows a write to a car's working copy: the archive goes on the resource editor's build
    /// list, and when the editor has the car on its stage it is loaded again from disk WITHOUT saving what
    /// the stage holds (its copy of the scene is the stale one now). True when a reload was started; the
    /// stage's undo history does not survive one.</summary>
    bool Landed(string car);
}

public sealed record CarLightInfo(string Bone, int Kind, string KindName, string Side, float Power, float Middle, float Speed0,
    float Speed1, string Model, int BreakParticle, int Last, IReadOnlyList<string> CheckBones);

public sealed record CarLightsInfo(string Car, string? ScaleBone, IReadOnlyList<CarLightInfo> Lights, IReadOnlyList<string> Bones);

/// <summary>One collision: what it is, its full size and where its middle stands in the model's space; for a
/// cooked hull the box that bounds it there, and why it cannot be changed.</summary>
public sealed record CarCollisionInfo(int Index, string Role, string Shape, float[] Size, float[] Position, float[]? Min, float[]? Max,
    string? ReadOnly);

public sealed record CarCollisionComponent(string Component, string Kind, string Bone, IReadOnlyList<CarCollisionInfo> Collisions);

public sealed record CarCollisionsInfo(string Car, IReadOnlyList<CarCollisionComponent> Components);

public sealed record CarWinterSwap(string Summer, string Winter, int Slots);

public sealed record CarWinterInfo(string Car, string Winter, string Reference, int FilesCopied, IReadOnlyList<CarWinterSwap> Materials,
    IReadOnlyList<string> TexturesAdded, IReadOnlyList<string> TexturesRemoved, IReadOnlyList<string> TexturesKept,
    IReadOnlyList<string> TexturesCarried, bool EffectsReplaced);

public sealed record CarMaterialRowInfo(string Material, string Hash, int Flags, string What, string Texture, bool OnTheCar);

public sealed record CarDeformRowInfo(string Material, string Hash, int Group, bool OnTheCar);

public sealed record CarMaterialsInfo(string Car, IReadOnlyList<CarMaterialRowInfo> Rows, IReadOnlyList<CarDeformRowInfo> Deform,
    IReadOnlyList<string> Unlisted);

public sealed record CarBoneInfo(string Car, string Name, string Parent, int Index, int Bones);

public sealed record CarBeaconInfo(string Car, string Casing, string Lamp, string Parent, float[] At, bool BonesAdded, bool LightAdded, int Bones);

public sealed record ArchiveTextureInfo(string Archive, string Texture, string Action, int Width, int Height, string? Format,
    IReadOnlyList<string> AlsoCarriedBy);

/// <summary>A sampler of a new material and the texture written for it.</summary>
public sealed record MaterialVariantTexture(string Sampler, string Name, string Texture, int Width, int Height, string Format, string From);

public sealed record MaterialVariantInfo(string Name, string Hash, string Source, string Library, string Archive,
    IReadOnlyList<MaterialVariantTexture> Textures);

public sealed record CarCheckMaterial(string Material, int Vertices, int UvSetsOutOfRange, int ForeignColours, int OffItsOwnBones);

public sealed record CarCheckLevel(int Lod, int Vertices, int Triangles, bool Skinned, int WeightsOffLattice, int Unskinned,
    int FarVertices, int ThinTriangles, int FlatUvTriangles, IReadOnlyList<CarCheckMaterial> AgainstReference);

public sealed record CarCheckInfo(string Car, string? Reference, bool Clean, IReadOnlyList<string> Problems,
    IReadOnlyList<string> ComparedAs, IReadOnlyList<string> NotCompared, IReadOnlyList<CarCheckLevel> Levels);
