using MgSprays;
int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
int spawns = 0, removals = 0;
var snapshot = new SpraySnapshot(123, "materials/mgsprays/test.vmat", 48, 96, 4, 10, 20, 30, 0, 90, 90, 100);
var visual = new SprayVisual<FakeDecal>(snapshot);
FakeDecal Spawn(SpraySnapshot saved) { spawns++; return new(saved); }
void Remove(FakeDecal decal) { removals++; decal.Alive = false; }
bool Valid(FakeDecal decal) => decal.Alive;
Check(visual.Refresh(100, 120, false, Valid, Remove, Spawn) == VisualRefresh.Recreated, "Initial spawn");
var original = visual.Entity!;
original.Alive = false; // Simulate CS2's round entity cleanup.
Check(visual.Refresh(160, 120, true, Valid, Remove, Spawn) == VisualRefresh.Recreated, "Restore after round cleanup");
Check(!ReferenceEquals(visual.Entity, original) && spawns == 2, "New engine entity created");
Check(visual.Entity!.Saved == snapshot, "Position, angles, image, size, owner, original birth all retained");
Check(CvarRules.Remaining(visual.Snapshot.PlacedAt, 160, 120) == 60, "Round restore does not restart lifetime");
var previous = visual.Entity;
Check(visual.Refresh(180, 120, true, Valid, Remove, Spawn) == VisualRefresh.Recreated, "Force refresh even if old entity remains valid");
Check(!previous!.Alive && removals == 1, "Force refresh removes old visual without duplication");
Check(visual.Refresh(181, 120, false, Valid, Remove, Spawn) == VisualRefresh.Unchanged && spawns == 3, "Watchdog leaves healthy visual untouched");
visual.Entity!.Alive = false;
Check(visual.Refresh(190, 120, false, Valid, Remove, Spawn) == VisualRefresh.Recreated, "Repair delayed cleanup after round callback");
Check(CvarRules.Remaining(visual.Snapshot.PlacedAt, 190, 120) == 30, "Repair keeps original deadline");
Check(visual.Refresh(220, 120, true, Valid, Remove, Spawn) == VisualRefresh.Expired && spawns == 4, "Expired sprays are not respawned at round start");
visual.Remove(Remove, Valid);
Check(visual.Entity == null, "Expiry/disable can remove restored visual");
var shortened = new SprayVisual<FakeDecal>(snapshot);
Check(shortened.Refresh(180, 60, true, Valid, Remove, Spawn) == VisualRefresh.Expired, "Shortened lifetime applies to logical spray");
var extended = new SprayVisual<FakeDecal>(snapshot);
Check(extended.Refresh(200, 300, true, Valid, Remove, Spawn) == VisualRefresh.Recreated, "Extended lifetime restores eligible spray");
Check(CvarRules.Remaining(extended.Snapshot.PlacedAt, 200, 300) == 200, "Extended deadline still uses original birth");
var failed = new SprayVisual<FakeDecal>(snapshot);
bool threw = false;
try { failed.Refresh(160, 120, true, Valid, Remove, _ => throw new InvalidOperationException("simulated spawn failure")); }
catch (InvalidOperationException) { threw = true; }
Check(threw && failed.Entity == null && failed.Snapshot == snapshot, "Failed recreation preserves saved spray for retry");
Check(failed.Refresh(161, 120, false, Valid, Remove, Spawn) == VisualRefresh.Recreated, "Watchdog can retry failed recreation");
Console.WriteLine($"Passed {checks} round-lifecycle checks.");
class FakeDecal(SpraySnapshot saved) { public SpraySnapshot Saved { get; } = saved; public bool Alive { get; set; } = true; }
