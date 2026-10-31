# Vehicle field evidence

The symbolized Mafia III Mac database was inspected only through its copied `.scratch/ida/mafia3_mac.i64`. Game resources were copied to `.scratch/vehicle-dynamics-fixture` before extraction or edits.

## Gearbox SystemObjectDatabase

`tables.sds/SystemObjectDatabase_96_BF3D541C89445A2B.bin` contains the `car_gearboxes` table. The serialized type GUID at file offset 12 is `0x3759788ec437536c`. The mounted vector starts at file offset 32; its first signed relative offset locates the rows, followed by a count. The shipped table contains twelve 148-byte rows. Unknown header bytes, row padding, and relocation/mount trailers are preserved.

Native evidence:

- `mafia::framework::C_CarGearboxesDatabase::GetCarGearbox`, `0x101c2ccd0`, indexes rows as `v9[37 * index] + *v9` (148-byte stride).
- `gamedb::tables::car_gearbox::S_CarGearboxTableItem::C_ThisTypeInfo::RegisterAttributes`, `0x100cf2db0`, provides the following exact names and row offsets.
- `C_Car::InitStaticCarTables`, `0x102b9ad10`, copies these fields into `S_CarGearbox` and stores the result in `C_CarTuningManager::s_VGearboxes`.
- `C_Car::ApplySettingsTable`, `0x102ba1160`, selects this gearbox and calls `C_Vehicle::SetGearboxSettings`.

| Row offset | Field | Storage |
| --- | --- | --- |
| 0 | m_ID | u32 |
| 4 | Description | fixed 32 bytes, kept read-only |
| 36 | m_Automatic | bool byte |
| 40 | m_GearCount | u32 |
| 44 | m_GearReverseCount | u32 |
| 48 + 12*i | m_GearRatio[i], i=0..6 | float |
| 52 + 12*i | m_RotationsGearUp[i] | float |
| 56 + 12*i | m_RotationsGearDown[i] | float |
| 132 | m_MinClutchGlobal | float |
| 136 | m_MinClutchAngleCoeffGlobal | float |
| 140 | m_ShiftDelayMin | float |
| 144 | m_ShiftDelayMax | float |

The isolated verification probe changed all 336 writable fields across the twelve rows and restored the complete original file byte for byte. Saving/reopening a ratio edit changed only its four-byte field; non-finite floats are rejected.

## Fixed C_Car initialization data

`C_EntityFactoryRecord<C_Car,10u,&C_CarName,3956u>::GetStaticSize`, `0x102bec4a0`, verifies entity type 10 and a 3956-byte payload. The reflected byte array carries a four-byte 3956 size prefix, for 3960 total bytes. Axle wheel model names begin at array offsets `0x240` and `0x260`, each in a fixed 32-byte UTF-8 field. Editors preserve original padding when undoing text changes and reject NULs or values longer than 31 UTF-8 bytes.

The payload is consumed directly by `C_Car::ApplySettingsTable`, `0x102ba1160`, including calls to mass, motor, gearbox, steering, and axle differential setters. It must not be dismissed as unused legacy data.

The following payload-relative scalar inputs are exposed. Add four for reflected byte-array offsets. `ApplySettingsTable` loads the mass at `0x20` into the mass settings and passes the engine values to `C_Vehicle::SetMotorSettings` (`0x101581e50`). This calls `C_Motor::SetMotorSettings` (`0x1015230c0`), which converts the three RPM inputs to angular velocities and derives torque from power and maximum engine speed. Tuning modifiers scale several of these values before the setter call.

| Payload offset | Exposed field |
| --- | --- |
| 0x20 | Mass |
| 0x54 | Power |
| 0x5c | RotationsMin |
| 0x60 | RotationsMax |
| 0x64 | RotationsMaxMax |
| 0x68 | TorqueStart |
| 0x6c | TorqueMax |
| 0x70 | TorqueEnd |
| 0x74 | RotationsTorqueLow (percent of RPM limit) |
| 0x78 | RotationsTorqueHigh (percent of RPM limit) |
| 0x7c | MotorBrakeTorque |
| 0x80 | MotorInertia |

`0x58` is deliberately unnamed pending interpretation. Unknown bytes remain preserved. All fourteen exposed engine/mass/wheel fields were individually changed and undone on both copied Bruno Ravine Sheriff and Smith Mainline resources; the full 3960-byte arrays returned byte for byte to their originals. Non-finite floats and invalid text were rejected without mutation.

Additional verified consumers in `ApplySettingsTable` supply fuel fields at `0x90/0x94`, default gearbox counts at `0xb0/0xb4`, seven ratio/up/down arrays at `0xb8/0xd4/0xf0`, shift delays at `0xa8/0xac`, and the five brake settings at `0x778..0x788`. `C_Vehicle::SetBrakeSettings`, `0x101582f40`, retains torque and handbrake torque and stores the reciprocal reaction time. A selected tuning gearbox overrides the default gearbox fields; the property tooltips state this.

Per-axle settings follow the number of axles in the reflected prefab, up to ten. `ApplySettingsTable` constructs `S_AxleSet` and passes it to `C_Vehicle::SetAxleSettings`, `0x1015823d0`. The scalar arrays have four-byte elements: spring stiffness at 1212, spring length at 1252, preload at 1292, compression damping at 1332, rebound damping at 1372, differential index at 1412, anti-roll torque at 1452, toe-in at 1532, kingpin inclination at 1572, caster at 1612, camber at 1652, and left/right brake coefficients at 1772/1812. Steering and handbrake flags are byte arrays at 1892/1902. These offsets are native payload-relative and include the first array element (the decompiler's backward loop uses a base four bytes earlier). Length/preload are divided by 100 before applying, and alignment angles are converted from degrees by the setter. The stock two-axle fixtures expose 76 dynamics fields; all 152 field edits across both fixtures were changed and undone byte exactly.

## Car tuning item table and gearbox selection

`tables.sds/SystemObjectDatabase_143_3DD6D47814442B4A.bin` is an `S_Tables` resource with serialized type GUID `0x5d69a41cdc82936c`. Its native root begins at file offset 32. `mafia::tables::C_TablesDatabase::GetCarTuningItemTable`, `0x101c34ec0`, resolves the signed relative table pointer at root+36 (file 68). The table is a relative vector with twelve-byte header and 212-byte rows, confirmed by `C_Car::InitStaticCarTables`. The shipped vector contains 117 rows. The adapter preserves every other table and the resource's relocation trailer.

`gamedb::tables::car_tuning_item::S_CarTuningItemTableItem::C_ThisTypeInfo::RegisterAttributes`, `0x100c2b6f0`, names every exposed attribute and provides its exact row offset. `m_Gearbox` at row offset 108 selects the gearbox table ID. The rows named `gearbox_3speed`, `gearbox_4speed`, etc. reference IDs 1, 2, etc.; reserved IDs 13–15 also exist in tuning rows even though the currently shipped gearbox vector has twelve definitions. Unresolved IDs retain the default settings, matching the native lookup behavior.

The adapter exposes 39 writable fields per row: gearbox ID, flags, engine/torque/turbo modifiers, final gear and viscous clutch, front/rear spring stiffness/length/damper/sway-bar/tyre-pressure modifiers, brake modifiers, spoiler/aerodynamic modifiers, material IDs, vehicle mass, engine resistance, deformation stiffness, and crash-speed modifiers. Row ID and slot ID remain read-only. All 4563 writable fields were changed and restored byte exactly. Saving/reopening a gearbox-selection edit changed only its four-byte integer.

The raw dynamics adapter now exposes 97 fields for the two-axle stock cars. Additional defaults include the center-of-mass vector at `0x24` (confirmed through `SetMassSettings` → `UpdateWeight`, `0x10152d880` → `UpdateMassAndCOM`), final gear count at `0x98`, two final ratios at `0x9c/0xa0`, automatic flag at `0xa4`, seven per-gear minimum-clutch values at `0x128`, seven minimum-clutch angle coefficients at `0x144`, and global clutch defaults at `0x160/0x164`. `ApplySettingsTable` directly copies the per-gear arrays into `S_GearBox` and falls back to the global clutch values for negative per-gear entries. The native initializer has space for two final ratios and seven gear slots, and the editor enforces those bounds. All 194 field edits across Bruno and Smith were changed and undone byte exactly. UI groups separate engine/mass, fuel, default gearbox, brakes, and each active axle.

## Collision geometry, placement, and editing

Collision element definitions reference shape objects by the first eight bytes of their twelve-byte `m_ItemDesc` value. Supported shipped vehicle shapes are convex (`0x48430eeb`), box (`0x63ee7798`), and capsule (`0x4fd32ee0`). `m_DataHash` identifies the compiled resource containing cooked data; `m_DataIndex` selects a root variant in its Havok packfile. The cooked resource is reflected type `0xaf6549f7` with `m_Data` containing a 64-bit, little-endian `hk_2014.2.0-r1` packfile.

The new reader follows the packfile's section directory, local/global pointer fixups, root named variants, and `hknpConvexPolytopeShape` relative arrays. It reads actual polygon vertex indices and triangulates each face; it does not substitute AABB boxes or recalculate a convex hull. The Windows serialized vertex/face/index relative-array fields are at 48/68/72. The symbolized Mac native base is eight bytes smaller: `hknpConvexShape::getNumberOfSupportVertices` (`0x1045f5550`) reads offset 40; `getSupportVertices` (`0x1045f5560`) returns the relative vertex array; `hknpConvexPolytopeShape::getFaceVertices` (`0x1045c9c60`) reads four-byte face records and byte vertex IDs. Serialized Windows shape bounds match reflected convex AABBs minus the stored 0.01 convex radius. The overlay shows those polygon core surfaces; the narrow rounded collision margin is retained as metadata rather than synthesized triangles.

Box `m_Size` contains half-extents, confirmed by comparison with its cooked hull and convex margin. `C_RBCapsuleElementDesc::GetLocalAABB` (`0x10144a340`) explicitly builds Z bounds `[-height/2-radius, height/2+radius]` and X/Y bounds `[-radius,+radius]`; capsules therefore use local Z and their height is the segment length between hemisphere centers. Capsule surfaces are tessellated for the overlay.

`S_ElementDef::S_LoadInPlaceData::RegisterAttributes` (`0x1013ab610`) verifies `m_ParentBoneId` at 14, `m_ParentBoneHashName` at 16, and `m_CollMatrix` at 24 in the 88-byte data value. `C_CollisionComponent::ActivateElement` (`0x101396ae0`) copies this matrix directly into `CreateElementInternal` (`0x101396d00`), which combines it with the shape's own local matrix. The matrix already contains model bind placement: multiplying original parent-bone transforms again incorrectly doubles the height of roof helper boxes. Parent bone ID is exposed read-only as attachment metadata. The overlay uses shape-local × collision matrix × owning frame world, with current canonical frame transforms read on every redraw.

Each collision row exposes a canonical transform adapter and shape properties. Shape material/layer/flags, capsule radius/height, and box half-extents are editable. Cooked indices, hashes, and vertex count remain read-only, and invalid dimensions/non-finite values are rejected. Moving/scaling a volume updates its own matrix in place; shape scalar changes are reflected on the next overlay redraw.

Isolated collision fixture verification:

| Copied vehicle | Volumes | Overlay triangles | Collision world bounds | Visual world bounds |
| --- | --- | --- | --- | --- |
| Bruno Ravine Sheriff | 11 | 710 | (-0.932,-2.156,-0.085)..(0.933,1.871,1.772) | (-1.011,-2.297,-0.333)..(1.011,2.090,1.777) |
| Smith Mainline | 9 | 136 | (-0.927,-2.812,0.126)..(0.992,2.501,1.563) | (-1.057,-2.952,-0.102)..(1.123,2.671,1.570) |

Both fixtures passed volume movement, live radius/box-size updates, save/reopen, and byte-exact restoration of all compiled resources. The standalone Havok probe verified all nine Bruno polygon hulls and rejects truncation, invalid section counts, and out-of-range face vertex indices. These tests used `.scratch/vehicle-collision-fixture`; original game files and the main shared fixture were untouched.

## Actual Blender GUI and MCP round-trip verification

An official portable Blender 4.5.15 LTS was unpacked under `.scratch/blender` without installation or changes to normal Blender preferences. Its SHA-256 matched the official release manifest (`52dd02fd74877fa08e74b67d7854cf32468a232c02aaa19b6844c91285574f8d`). The toolkit ran from an isolated output folder with its own settings, MCP port 12014, `BLENDER_USER_CONFIG`, and the new optional `ILLUSION_BRIDGE_DIR` discovery/exchange override. When the override is absent both applications retain their existing discovery location.

The actual Blender GUI imported `built_bc_bayou_block_a_baked_0` from a copied Bayou LOD2 archive. Tests exercised a manual Edit Mode vertex move, authored custom corner normals and UVs, and a face poke adding a vertex and two triangles. Actual addon payloads went through the live MCP `blender_push`, `editor_save`, and `editor_build` workflow. The final mesh contained 2580 packed vertices and 1573 triangles. A separate copy of the built archive was freshly extracted and decoded: all UV corners matched the actual Blender payload within 0.000130, and all nonzero authored normals within 0.005546 (native packed quantization). Nine pre-existing degenerate-face zero normals use the valid normal fallback. Two live undo operations and save restored the complete Generic resource byte exactly; the isolated source archive was also restored from its baseline. No game originals were written.

This test exposed two real integration issues. Blender's custom-normal encoding introduces approximately 1e-4 corner noise, which previously caused a position-only edit to create false split vertices. Compiled mesh editing now snaps near-original normals and compares repeated edited source normals within one quarter of the native eight-bit normal step. The captured actual GUI position edit now stays count-preserving (18 packed vertices change including rebuilt tangent neighbors), with byte-exact undo. The importer also frames the imported objects rather than unrelated default-scene objects and expands view clipping for city meshes far from the origin; a fresh GUI import visibly framed the whole Bayou mesh.
