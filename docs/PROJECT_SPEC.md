# Raid Debrief — Project Specification

## 1. Product Definition
Raid Debrief is a post-pull immediate replay FF14 Dalamud plugin focused on high-end raid post-pull review.

The core question it should answer is:

> What happened in that pull, and can I replay it immediately?

The plugin is not intended to teach mechanics, judge player mistakes, or replace FFLogs/ACT.

## 2. Primary User Flow
1. A pull starts.
2. The plugin records combat state and events.
3. The pull ends in a wipe or clear.
4. A compact Debrief Summary becomes available.
5. The player sees the first death and subsequent death sequence.
6. The player opens Replay.
7. Replay starts near the important event window instead of forcing review from 00:00.
8. The player inspects positions, waymarks, current target markers, deaths, casts, statuses, and timeline events.
9. When the next pull begins, Raid Debrief must not interfere with combat.

## 3. Core UI
Target summary concept:

```text
┌────────────────────────────────────┐
│ Pull #18        05:32       31.8% │
│                                    │
│ FIRST DEATH                        │
│ D2                     04:51       │
│                                    │
│ D2 ☠ → H1 ☠ → MT ☠ → WIPE         │
│ 04:51   04:54   04:59   05:03     │
│                                    │
│     [ Replay 04:43 → 05:03 ]       │
└────────────────────────────────────┘
```

The exact visual design may change, but the information hierarchy should remain simple.

The History window opens from the Replay page's top-left `歷史紀錄` button, groups validated automatic Pulls by one uninterrupted Duty visit, shows the newest visit and Pull first, and lets the player load a selected Pull into the same Replay presentation. Its table reports objective end-state data: Pull timing and the final Boss HP percentage when exactly one Boss candidate is identifiable; it shows `—` rather than guessing when no unique candidate exists.

## 4. MVP Features
Runtime replay still prioritizes the latest successfully completed Pull. The approved post-MVP History scope additionally persists validated automatic Pulls, groups them by Duty visit, and provides browsing and Replay selection; retention controls remain a later phase.

### 4.1 Combat Recording
Record enough data to reconstruct the pull after it ends.

Required targets:
- Party members
- Boss actors
- Position
- Facing direction
- HP and barrier percentage
- Death
- Raise
- Cast start/end
- Damage/heal events when reliably obtainable
- Status gain/loss when reliably obtainable
- Pull-local Action names for observed casts when reliably resolvable
- Combat start/end
- Waymarks

### 4.2 Debrief Summary
Required:
- Pull number
- Pull duration
- Boss HP at end if available
- First death
- Death sequence until wipe
- Replay shortcut around the relevant time window

The summary must remain compact and must not become a combat report dashboard.

Phase 4 presents recorded observations only. Pull duration is the latest recorded timestamp or finalized wall-clock duration, whichever is greater. Boss HP is shown only when exactly one replay-visible non-player-owned BattleNpc with valid HP can be identified at the final observed state; otherwise it is unavailable rather than guessed. Death ordering uses recorded player `Death` events and preserves repeated deaths after a raise. The shortcut is bound to the same in-memory CaptureId／completion generation and opens paused at the suggested start.

### 4.3 Replay
Replay is the main product.

Required:
- Three-column Replay layout with compact Pull／Party context, dominant Arena, and Actor／Death context; both side columns hold one fixed width so their cards never resize with the window, the Arena absorbs every additional pixel, and the enforced minimum height keeps the complete Death context readable without a side-panel scrollbar
- Boss name, HP, and recorded cast progress above the Arena
- Responsive square 2D arena view with Player／Boss positions, facing, dead state, Waymarks, selected／hovered Actor priority, collision-suppressed labels, and compact Boss HUD treatment at small canvas sizes
- Bottom-only playback controls, speed, single Timeline, scrubbing, and player-death markers
- Complete player-death quick-jump list with direct paused seek
- Timestamp Party and selected-player HP／alive／barrier state plus recorded active mitigation and recovery support
- Confidence-labelled immediate Death correlation built from recorded HP and Action Effects

The formal Replay surface exposes only recorded player deaths for Timeline navigation. Selecting a death focuses the Actor, seeks exactly to the recorded Death timestamp, pauses playback, and opens its context. Cast and status events reconstruct the state at the selected timestamp but are not separate navigation markers, with one recorded exception: because FFXIV strips every status on death, the Death context reconstructs its status rows at the killing blow's recorded Action Effect timestamp and ignores the status removals the Death transition itself caused. That remains an observation of the last recorded status sample, never a claim that the mitigation was or was not sufficient. Correlated Killing Blow／HP-before-hit／Overkill values are derived interpretations, never server-authored fields: the model preserves confidence, evidence, limitations, and an algorithm version, while the UI labels estimated values and never infers responsibility, mechanic cause, or recommended action. Runtime／JSON source diagnostics remain collapsed under the advanced development section.

Current Captures preserve barrier percentage and one reliable Pull-local Action-name snapshot per observed Action ID. Replay must prefer that recorded name over current-session localization and must never expose unresolved RSV placeholders as names. Status presentation is reconstructed from recorded transitions: player defensive／barrier／invulnerability effects are objective observations, healing-over-time display is optional and disabled by default, and Boss effects are limited to recorded damage-down debuffs. These rows are descriptive state, not mitigation scoring or cause attribution.

### 4.4 Death Jump
Users can jump directly to every recorded player `Death`, including repeated deaths after a raise. The single Timeline and Quick Jump surface contain player deaths only; raises, casts, status changes, Wipe, damage, and heal events are not navigation markers. Quick Jump never infers MT／D1-style positions. A single death is a compact Job-icon／abbreviation／timestamp chip and seeks directly. Deaths no more than five seconds from the first death in a group collapse into one chronological cluster chip with a death icon, derived title, timestamp range, and count badge; activating it opens the exact Job／timestamp choices. All chips share one height while width follows their content. A group of exactly eight deaths uses the presentation label `WIPED`; this label derives from the death count and is not a recorded `DutyWiped` event.

## 5. Event Chain
### Core behavior
Event Chain is an ordered sequence of observed events.

Example:

```text
04:51 D2 Death
04:54 H1 Death
04:59 MT Death
05:03 Wipe
```

This is chronological correlation, not causal attribution.

### Extra Goal
Later versions may attempt higher-level event correlation.

Example:

```text
D2 Death
↓
Debuff Lost
↓
Mechanic resolves 30s later
↓
Wipe
```

Any inference must be explicitly distinguishable from directly observed data.

Suggested confidence levels:
- Observed
- High-confidence inferred
- Unsupported / never shown

## 6. Product Principles
- Replay first.
- Fast post-wipe inspection.
- Objective data over blame.
- Minimal interruption between pulls.
- No requirement to understand every fight mechanic.
- Generic architecture should work across encounters.

## 7. Explicitly Out of Scope
Do not implement unless the project scope is explicitly changed:
- Pull Compare
- Ghost Replay
- Best-pull overlays
- DPS meters
- Rotation analysis
- AI analysis
- Automatic blame assignment
- Mechanic instructions
- Strategy recommendations
- Session progression statistics
- Achievement/progress tracking

## 8. UX Constraints
- Opening Raid Debrief must never steal focus during combat.
- Replay windows should auto-hide on combat entry by default or remain visible but paused when the user disables auto-close. Explicit opening is always rejected during combat; changing the setting after combat begins does not create a second combat-entry action.
- Saving or processing a pull must not create visible stutter.
- A player should be able to reach the relevant replay moment within a few seconds after a wipe.
- After a Wipe Pull finalizes successfully, one compact action should let the player open that same Runtime Replay; the action must disappear when combat starts.
