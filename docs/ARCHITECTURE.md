# Architecture and experiment limits

```text
User's game -> native bridge -> local state.json -> explicit state filter
                                                    |
                                         forecasts + Jev request
                                                    |
User's game <- bounded input <- local command.json <- typed answers
                                                    |
                                           local log / dashboard
```

The bridge runs on Unity's main thread. It measures the knight's position, velocity, bounds, grounded state, available abilities and input timing; nearby geometry; enemy bodies, velocity and exposed state-machine state; and arena bounds. It has no provider credentials or network client. The controller constructs a new request from a field allowlist. Filesystem paths, save contents and arbitrary envelope/debug fields are not model observations.

The Gruz Mother controller asks for movement and a feasible nail strike independently. Standing, walking, jumping, releasing and repressing jump, and available dash variants are represented as finite controls. Strike selection includes none, the current facing direction, up, and airborne down. Nail is the only connected weapon; spells are not implemented. A displayed mode is a summary of selected controls, not an additional model decision.

Local physics forecasts compare possible knight controls with projected boss motion branches. They include response delay, walls, jump press/hold semantics, recoil, dash availability and a separate proximity margin. These forecasts are estimates from measured state and explicit assumptions; they are not access to future RNG or a guarantee of collision avoidance. The model sees estimates for alternatives and selects controls. There is no hidden route hint or scripted boss-fight solver.

Commands carry session, observed sequence, timestamp and lease duration. Native validation rejects invalid, stale, wrong-session or disabled commands. A lease lasts at most 300 ms. No fresh accepted action means controls expire. STOP/F8 latches control off. Only an owned slot-4 session and an allowed scene can enable control.

Arena preparation, scoring and save ownership are local harness functions, independent of model choices. Native normal-health hitless verification stops on damage and requires the native victory event with zero hits. Training can replenish health and collect longer traces, but reports `training_complete`, never verified success. A quiet zero-hit segment is not a win.

The included tests cover synthetic state filtering, finite input contracts, stale/session rejection, cancellation during requests, attack and jump semantics, forecasts and budget handling. They do not establish correct behavior for every game state. The portable installer has build-time compatibility checks; a fresh live install and full fight must still be validated by its operator.

Useful comparisons for future experiments include damage per hit, time to first hit, damage per second, idle time while a strike is feasible, corner occupancy, invalid-action/ack rate, prediction false negatives, and calls or estimated cost per damage point. Preserve a baseline, change one policy component, and compare equivalent starting conditions. More aggressive combat pursuit remains future work.
