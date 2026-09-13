# Seasonal thermal physics

DV99 simulates locomotive heat but assumes a fixed outside temperature of 25 C.
DVSeasons feeds the synchronized seasonal air temperature into those native
calculations. It patches simulation values rather than gauges, so existing damage,
overheat protection, save data and readouts continue to use the resulting real
temperatures.

## Patched native components

- `LocoSim.Implementations.PassiveCooler`, `ActiveCooler`,
  `AutomaticCooler` and `DirectionalMovementCooler`: the native cooler output is
  corrected from its 25 C reference to the seasonal ambient. This affects the
  `HeatReservoir` instances used by diesel engines, hydraulic systems and traction
  motors. Cooler capacity and fan state remain native.
- `LocoSim.Implementations.HeatReservoir`: a newly streamed reservoir starts at
  ambient temperature. DV applies saved reservoir temperatures after construction,
  so a loaded locomotive retains its saved heat.
- `LocoSim.Implementations.Boiler.SimulateSteamConsumption`: boiler casing loss
  uses seasonal ambient instead of the hard-coded 25 C reference.
- `LocoSim.Implementations.Firebox.ComputeTemperature`: intake air shifts the
  combustion equilibrium by 0.35 C for every 1 C difference from DV's baseline.
  The native 500 C minimum combustion temperature remains enforced. The reduced
  coupling represents a hot fire being much less sensitive to air temperature
  than an unpowered engine or brake block. While the fire is lit, its native response time also changes
  by 0.4% per degree from 25 C (bounded to 0.8x..1.3x), so a -30 C fire warms 22%
  more slowly even while its target is at the 500 C combustion floor.
- `DV.Simulation.Brake.BrakeSystem.HeatController`: brake-block heat generation
  remains `speed * brake factor * 1.5`. Newton cooling keeps the native coefficient
  `0.004 / s`, but its reference and minimum temperature follow seasonal ambient.
  The native 600-1000 C fade range and 12% minimum braking factor are unchanged.

At 25 C every patched calculation produces the original DV99 result. Ambient is
sanitized to -40..45 C; invalid network values fall back to 25 C. Harmony patches
are installed only during a live season session and removed when the mod or world
is unloaded. A layout mismatch disables this feature as one unit and logs one
warning rather than leaving a partial set of thermal patches active.
