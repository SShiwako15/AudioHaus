# Short demonstration

This is a five-minute release smoke demonstration for an unlocked Windows
desktop. It is intentionally short: it shows the user-visible routing flow,
not a substitute for the full validation procedures.

## Prepare

1. Install VB-CABLE separately and complete any requested restart.
2. Connect headphones and speakers. Keep a physical device as the Windows
   default and verify **Listen to this device** is off for **CABLE Output**.
3. Launch AudioHaus and two source applications with obvious audio, such as a
   browser video and a local media player. Keep the source volume moderate.

## Demonstrate

1. In AudioHaus, select the first source on the right and headphones on the
   left, then click **Connect**. Confirm the route row appears and the source
   meter moves.
2. Select the second source and speakers, then click **Connect**. Confirm both
   routes remain visible and the destination labels are distinct.
3. Connect the first source to speakers as a second destination. Confirm the
   source shows fan-out while the two output routes remain independent.
4. Lower the first source volume and mute/unmute it. Its routed copies should
   change together while the second source continues.
5. Mute the speakers output. The source meter can remain active, but the
   speaker output should be silent; unmute it before continuing.
6. Disconnect one fan-out destination, then close AudioHaus normally.

## Expected result

- Each source is heard only at its selected destination(s).
- Fan-out copies the source to both destinations; it does not duplicate the
  source card or create an unexpected Windows default-device change.
- Source controls affect all copies of that source; output controls affect only
  the selected mix.
- Closing AudioHaus stops its routes and restores the original applications'
  render preferences. The source applications remain running.

If physical hardware is unavailable, run the software-only desktop checks
described in [DesktopChecks README](../tests/AudioHaus.DesktopChecks/README.md)
to exercise discovery, selection, controls, fan-out, and cleanup. Software
meters do not prove that a physical speaker is audible.

For a longer listening session, device outage recovery, or known limitations,
use [DesktopSetup.md](DesktopSetup.md) and
[DesktopValidation.md](DesktopValidation.md).
