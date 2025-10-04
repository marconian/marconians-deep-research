# CAPTCHA Stealth Hardening Plan

This plan tracks the engineering work required to harden the Marconian computer-use stack against modern bot detection, aligning with milestone M18 of the main development plan.

## Workstreams

### 1. Browser Launch Hygiene
- [ ] Finalize hardened Chromium launch profile (remove `--enable-automation`, add `--disable-blink-features=AutomationControlled`, enable persistent contexts).
- [ ] Align locale, timezone, and viewport with proxy region and document defaults in `Settings`.
- [ ] Integrate the profile through `ComputerUseBrowserFactory` and add regression coverage via diagnostics.

### 2. Stealth Middleware Evaluation
- [ ] Prototype `soenneker.playwrights.extensions.stealth` and capture detection telemetry.
- [ ] Prototype `Undetected.Playwright` integration and compare results.
- [ ] Decide on adoption vs. custom patching; document trade-offs and maintenance plan.

### 3. Driver Patch Assessment
- [ ] Scope applying `rebrowser-patches` (or equivalent) to the bundled Node driver.
- [ ] Record patch application steps and reapply procedure post-Playwright upgrades.
- [ ] Validate Runtime.Enable leak mitigation via diagnostic run (`https://bot.sannysoft.com/`).

### 4. Behavioral Simulation Hooks
- [ ] Design cursor path generator (Bezier/noise) with configurable speed jitter.
- [ ] Implement typing cadence simulator for `text` actions.
- [ ] Route CUA-issued commands through simulators before forwarding to Playwright.
- [ ] Add unit tests or deterministic diagnostics for simulator behavior.

### 5. Network Identity Alignment
- [ ] Integrate residential proxy provider into configuration and secret management.
- [ ] Ensure TLS/JA3 fingerprint alignment with claimed browser via proxy selection.
- [ ] Add health checks and fallback logic when proxy pool is exhausted.

### 6. CAPTCHA Detection & Recovery
- [ ] Detect CAPTCHA elements/phrases during exploration and raise structured events.
- [ ] Provide retry guidance or human handoff hooks in transcript/logs.
- [ ] Backfill tests validating fallback behavior when CAPTCHA is encountered.

## Validation Checklist
- [ ] Diagnostic runs across stealth configurations documented with screenshots/logs.
- [ ] Plan outcomes reflected in `docs/architectural_patterns.md` where durable.
- [ ] `docs/specs.md` updated if mitigation changes external requirements.
