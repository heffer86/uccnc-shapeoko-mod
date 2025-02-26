﻿/**
 * Macro: M6 Tool Change
 * UCCNC v1.2115 or higher required, with Messages plugin enabled
 * 
 * Unload current tool to load new tool
 * M6
 * 
 * Unloading Current Tool:
 *  If tool has "NoStore" tag in tool type field, will prompt for manually unloading tool
 *  If current tool has no slot assigned, it is assigned next empty slot (profileAutoAssignToolSlot=true)
 *  If no empty slots are available, will prompt for manually unloading tool
 * 
 * Loading New Tool:
 *  If new tool is not in a slot, will prompt for manually loading tool
 *  Loaded tool keeps assigned slot to be placed at on unload
 *  If ATC unload was detected to be unsuccessful, additional attempts will be made before failing
 * 
 * Tool Detection:
 *  Checks that tool was successfully ejected for both auto and manual tool unloading
 *  Checks that tool was successfully clamped for both auto and manual tool loading
 *  Checks that tool is clamped if new tool is same as current tool
 *  Sensor states are debounced with a given amount of time to meet their expected state before failing
 * 
 * atcLEDToolClamp=false and atcLEDToolRelease=false
 *  drawbar overtraveling above clamp sensor (no tool) or
 *  not reaching full extension to release sensor (jam/low air pressure)
 *  depending on 'atcOutputToolRelease' state
 * 
 * atcLEDToolClamp=true and atcLEDToolRelease=false
 *  drawbar petal clamped tool, travel stopped at clamp sensor
 * 
 * atcLEDToolClamp=false and atcLEDToolRelease=true
 *  drawbar petal unclamped, travel bottoming out at release sensor
 * 
 * NOTE: Tool table slot data is saved in custom profile section due to limitations in UCCNC API when auto-assign is enabled
 * Custom screenset fields can be used to represent tool table with macro to load and save changes (see M21050 and M21051)
 */

// ### M6 CONFIG ###

#define DEBUG_CSX
#if DEBUG_CSX
public class Macroclass {
  protected UCCNC.Executer exec;
  protected UCCNC.AS3interfaceClass AS3;
  protected UCCNC.Allvarstruct Allvars;

  public void Runmacro() {
#endif

    // UCCNC has limitation in tool table API (http://forum.cncdrive.com/viewtopic.php?f=13&t=3537)
    // if auto-assign is enabled, the UCCNC tool table can't be used to manage the tool slot number
    // WARNING: DO NOT CHANGE AFTER A TOOL IS ASSIGNED A SLOT - STORED STATE OF TOOL SLOTS AND TOOL RACK WILL NOT MATCH
    var profileAutoAssignToolSlot = true;
    // we are resorting to using a custom section for tool slot in the profile to prevent losing data if auto assign is enabled
    var profileToolTableSlotSection = profileAutoAssignToolSlot ? "M6ToolTable" : "Tooltablevalues";

    // should restore to original the XY position after successful tool change
    var restoreXYPosition = false;
    // should apply G43 tool offset after successful tool change
    var applyToolOffset = false;

    // macro to use for tool offset probe
    var toolOffsetProbeMacro = "M31";
    // should ALWAYS run tool offset probe macro after a successful tool change
    var toolOffsetProbeAfterToolChange = false;

    // macros executed before unloading current tool and after new tool loaded
    // can be used to retract/remove dust boot and detract/reinstall
    var toolChangePreMacro = "";
    var toolChangePostMacro = "";

    // safe Z position for travel
    double zSafe = 0.0D;

    // Z position to release tool
    double toolChangeReleaseZ = -6.02D;
    // Z position to clamp tool
    double toolChangeClampZ = toolChangeReleaseZ - 0.05;

    // feedrate when moving to or out of tool release/clamp position
    int toolChangeFeedRate = 20;

    // distance above tool slot position to start tool change procedure with air purge and toolChangeFeedRate
    double toolChangeOffsetZ = 0.5D;

    // Whether to use toolChangeSlotOffset as side-[un]loading tool holder capture
    // for side loading, change toolChangeSlotOffset:
    //     (-1.0,0) for X side loading that starts offset from the tool slot by X-1.0 units
    //     (0.-1.0) for Y side loading that starts offset from the tool slot by Y-1.0 units
    var toolSlotIsSideLoad = false;

    // tool slot position offset for inital move before going to actual slot position
    // - can be used for side-[un]loading tool holder forks
    // - can be used to help reduce chances of traveling over tool rack
    // - ensure toolSlotIsSideLoad matches your setup!
    var toolChangeSlotOffset = new Position(0.0D, 0.0D);

    // tool slot XY positions
    var toolSlots = new Position[]{
        // Slot 0 does not exist
        new Position(double.NaN, double.NaN),
        // Slot 1
        new Position(-18.0015D, -0.1683D),
        // Slot 2
        new Position(-15.8436D, -0.1732D),
        // Slot 3
        new Position(-13.6899D, -0.1621D),
        // Slot 4
        new Position(-11.5367D, -0.1700D),
        // Slot 5
        new Position(-9.3689D, -0.1700D),
        // Slot 6
        new Position(-7.2179D, -0.1500D),
        // Slot 7
        new Position(-5.0614, -0.1500D),
        // Slot 8
        new Position(-2.9068D, -0.1600D),
    };

    // manual tool change position
    var toolManualPosition = new Position(-9.0D, -18.0D);
    // enable prompts to engage tool release and clamp
    var toolManualATCPrompts = false;
    // prompt before moving to toolManualPosition
    var toolManualPromptBeforeMove = false;
    // enable prompt for tool offset probe after manaul tool change
    var toolManualToolOffsetProbePrompt = true;

    // OUTPUT to engage tool release
    var atcOutputToolRelease = new PortPin(3, 1);
    // OUTPUT to engage tool clamp - optional, if null it expects atcOutputToolRelease off will cause tool clamp
    // i.e. = new PortPin(3, 2);
    PortPin? atcOutputToolClamp = null;
    // OUTPUT to engage air purge
    var atcOutputPurge = new PortPin(3, 3);

    // delay in milliseconds before turning atcOutputToolClamp off - 0 = do not turn off
    var atcToolClampOffDelayMS = 0;
    // delay in milliseconds before turning atcOutputPurge off - 0 = do not turn off
    var atcPurgeOffDelayMS = 1500;

    // INPUT to detect if a tool is clamped (96=InputPT3PN11)
    var atcLEDToolClamp = 96;
    // INPUT to detect tool release (97=InputPT3PN12)
    var atcLEDToolRelease = 97;

    // Interrupt from failure: 130=Cycle Stop or 512=RESET
    var buttonInterrupt = 130;
    // Cancel from user: 130=Cycle Stop or 512=RESET
    var buttonCancel = 130;

    // ### M6 MACRO ###

    // check if machine is homed/referenced
    if (!exec.GetLED(56) || !exec.GetLED(57) || !exec.GetLED(58)) {
      exec.Callbutton(buttonInterrupt);
      Prompt("M6: Tool Change Failed", "Machine is not homed", "OK", PromptStatus.Error);
      return;
    }

    while (exec.IsMoving()) { };
    // store original XY position and modal to restore
    var originalPosition = new Position(exec.GetXmachpos(), exec.GetYmachpos());
    var originalModal = AS3.Getfield(877).Split('|');

    var toolCurrentNumber = exec.Getcurrenttool();
    var toolNewNumber = exec.Getnewtool();

    // Check if there is a tool in the spindle
    if (toolCurrentNumber > 0 && !AssertClampPosition(atcLEDToolClamp, true, atcLEDToolRelease, false)) {
      exec.Callbutton(buttonInterrupt);
      Prompt("M6: Tool Change Failed", "Tool not in spindle", "OK", PromptStatus.Error);
      return;
    }

    // if same tool, do nothing
    if (toolCurrentNumber == toolNewNumber) {
      return;
    }

    if (!ExecuteGCode(
        // execute any pre-macro
        toolChangePreMacro,
        // stop coolant and spindle
        "M9", "M5",
        // move to safe z
        "G90 G00 G53 Z" + zSafe,
        // cancel out tool offset, scale and rotation
        "G49 G50 G69"
    )) {
      exec.AddStatusmessage("M6: tool change interrupted");
      return;
    }

    // read the tool slot #
    var toolCurrentSlot = toolCurrentNumber > 0 ? int.Parse(exec.Readkey(profileToolTableSlotSection, "Slot" + toolCurrentNumber, "0")) : 0;
    var toolNewSlot = toolNewNumber > 0 ? int.Parse(exec.Readkey(profileToolTableSlotSection, "Slot" + toolNewNumber, "0")) : 0;

    // read the tool desc
    var toolCurrentDesc = exec.Readkey("Tooltablevalues", "Description" + toolCurrentNumber, "");
    var toolNewDesc = exec.Readkey("Tooltablevalues", "Description" + toolNewNumber, "");

    // does the new tool require a manual tool change
    var toolNewIsManual = (toolNewNumber > 0 && toolNewSlot < 1);

    // check that tool slot not out of range
    if (toolCurrentSlot >= toolSlots.Length) {
      exec.Callbutton(buttonInterrupt);
      Prompt("M6: Tool Change Failed", "T" + toolCurrentNumber + " slot #" + toolCurrentSlot + " out of range", "OK", PromptStatus.Error);
      return;
    }
    if (toolNewSlot >= toolSlots.Length) {
      exec.Callbutton(buttonInterrupt);
      Prompt("M6: Tool Change Failed", "T" + toolNewNumber + " slot #" + toolNewSlot + " out of range", "OK", PromptStatus.Error);
      return;
    }

    // read and parse tool flags
    var toolCurrentTypeFlags = exec.Readkey("Tooltablevalues", "Type" + toolCurrentNumber, "").Split(new char[] { ' ', ',', ';' });
    var toolCurrentStorable = !Array.Exists(toolCurrentTypeFlags, f => string.Equals(f, "NoStore", StringComparison.InvariantCultureIgnoreCase));

    // create function for manual tool changes
    QTCDelegate ExecuteManualToolChange = (string status, ToolChangeAction action, int toolNumber, string toolDesc, bool skipClamp) => {
      exec.AddStatusmessage("M6: " + status);

      var result = DialogResult.None;

      var distanceX = Math.Abs(exec.GetXmachpos() - toolManualPosition.X);
      var distanceY = Math.Abs(exec.GetYmachpos() - toolManualPosition.Y);
      // check if not at manual tool position
      if (distanceX > 0.0001D || distanceY > 0.0001D) {

        result = toolManualPromptBeforeMove ?
                    Prompt("M6: Manual Tool Change", "Move to position for manual " + action.ToString().ToLower() + " of T" + toolNumber + "?\n" + toolDesc, "YesNoCancel", PromptStatus.Warning) :
                    DialogResult.Yes;

        if (result == DialogResult.Yes) {
          if (!ExecuteGCode("G00 G53 X" + toolManualPosition.X + " Y" + toolManualPosition.Y)) {
            exec.AddStatusmessage("M6: tool change interrupted");
            return false;
          }
        } else if (result != DialogResult.No) {
          exec.AddStatusmessage("M6: Manual tool " + action.ToString().ToLower() + " for T" + toolNumber + " was canceled");
          exec.Callbutton(buttonCancel);
          return false;
        }
      }

      if (!toolManualATCPrompts) {

        if (action == ToolChangeAction.Unload || !toolManualToolOffsetProbePrompt || toolOffsetProbeAfterToolChange) {
          result = Prompt("M6: Manual Tool Change", action + " T" + toolNumber + " and press OK to continue\n" + toolDesc, "OKCancel", PromptStatus.Warning);
        } else {
          result = Prompt("M6: Manual Tool Change", action + " T" + toolNumber + " and press Yes to measure tool offset, No to continue\nCurrent offset Z" + FormatD(GetOffsetZ(toolNumber)) + "\n" + toolDesc, "YesNoCancel", PromptStatus.Warning);
        }

        if (result == DialogResult.Yes) {
          // force tool offset probe
          toolOffsetProbeAfterToolChange = true;
        } else if (result != DialogResult.OK && result != DialogResult.No) {
          exec.AddStatusmessage("M6: Manual tool change for T" + toolNumber + " was canceled");
          exec.Callbutton(buttonCancel);
          return false;
        }

      } else {

        // check if in tool release position
        if (!AssertClampPosition(atcLEDToolClamp, false, atcLEDToolRelease, true)) {
          result = Prompt("M6: Manual Tool Change", (action == ToolChangeAction.Load ?
              "Press OK to open tool clamp" :
              "Secure T" + toolNumber + " and press OK to release tool") +
              "\n" + (action == ToolChangeAction.Load ? "" : toolDesc), "OKCancel",
              action == ToolChangeAction.Unload ? PromptStatus.Warning : PromptStatus.None // warning for unload - tool drop!
          );
          if (result != DialogResult.OK) {
            exec.AddStatusmessage("M6: Manual tool change for T" + toolNumber + " was canceled");
            exec.Callbutton(buttonCancel);
            return false;
          }
          ATCOpen(atcOutputToolRelease, atcOutputToolClamp, atcOutputPurge, atcPurgeOffDelayMS);
        }

        // check if skip reclamp - manual tool change back to back
        if (!skipClamp) {
          result = Prompt(
              "M6: Manual Tool Change", action + " T" + toolNumber + " and press OK to " + (action == ToolChangeAction.Load ? "clamp tool" : "continue") + "\n" + toolDesc, "OKCancel",
              action == ToolChangeAction.Unload ? PromptStatus.Warning : PromptStatus.None // warning for unload as motion will happen after
          );
          ATCClose(atcOutputToolRelease, atcOutputToolClamp, atcOutputPurge, atcToolClampOffDelayMS);

          if (result != DialogResult.OK) {
            exec.AddStatusmessage("M6: Manual tool change for T" + toolNumber + " was canceled");
            exec.Callbutton(buttonCancel);
            return false;
          }

          if (action == ToolChangeAction.Load) {
            if (!toolManualToolOffsetProbePrompt || toolOffsetProbeAfterToolChange) {
              result = Prompt("M6: Manual Tool Change", "Press OK to continue", "OKCancel", PromptStatus.Warning); // warning for motion after
            } else {
              result = Prompt("M6: Manual Tool Change", "Press Yes to measure tool offset for T" + toolNumber + ", No to continue\nCurrent offset Z" + FormatD(GetOffsetZ(toolNumber)) + "\n" + toolDesc, "YesNoCancel", PromptStatus.Warning);
            }

            if (result == DialogResult.Yes) {
              // force tool offset probe
              toolOffsetProbeAfterToolChange = true;
            } else if (result != DialogResult.OK && result != DialogResult.No) {
              exec.AddStatusmessage("M6: Manual tool change for T" + toolNumber + " was canceled");
              exec.Callbutton(buttonCancel);
              return false;
            }
          }
        }

      }

      // move back to safe Z - just in case
      if (!ExecuteGCode("G00 G53 Z" + zSafe)) {
        exec.AddStatusmessage("M6: tool change interrupted");
        return false;
      }
      return true;
    };

    if (toolCurrentNumber > 0) {
      if (!toolCurrentStorable) {
        if (!ExecuteManualToolChange("T" + toolCurrentNumber + " not storable, manual tool change required", ToolChangeAction.Unload, toolCurrentNumber, toolCurrentDesc, toolNewIsManual)) {
          return;
        }

        if (toolCurrentSlot > 0) {
          toolCurrentSlot = 0;
          exec.Writekey(profileToolTableSlotSection, "Slot" + toolCurrentNumber, toolCurrentSlot.ToString());
        }
      } else {
        // if auto-assign enabled and current tool slot is not assigned (zero)
        if (profileAutoAssignToolSlot && toolCurrentSlot < 1) {
          // get current used slots
          var usedSlots = new System.Collections.Generic.List<int>();
          for (var i = 1; i <= 96; i++) {
            var slot = int.Parse(exec.Readkey(profileToolTableSlotSection, "Slot" + i, "0"));
            if (slot > 0) {
              usedSlots.Add(slot);
            }
          }

          // find an empty slot #
          var emptySlot = 0;
          for (var i = 1; i < toolSlots.Length; i++) {
            if (!usedSlots.Contains(i)) {
              emptySlot = i;
              break;
            }
          }

          if (emptySlot > 0) {
            exec.AddStatusmessage("M6: assigning T" + toolCurrentNumber + " to empty slot #" + emptySlot);
            // save slot to current tool
            toolCurrentSlot = emptySlot;
            exec.Writekey(profileToolTableSlotSection, "Slot" + toolCurrentNumber, toolCurrentSlot.ToString());
          } else {
            exec.AddStatusmessage("M6: no available tool slots for T" + toolCurrentNumber);
          }
        }

        if (toolCurrentSlot < 1) {
          // release current tool manually
          if (!ExecuteManualToolChange("Tool not assigned slot, manual tool change required", ToolChangeAction.Unload, toolCurrentNumber, toolCurrentDesc, toolNewIsManual)) {
            return;
          }
        } else {
          // unload/release tool via ATC

          exec.AddStatusmessage("M6: unloading T" + toolCurrentNumber + " into slot #" + toolCurrentSlot);

          var toolSlotPosition = toolSlots[toolCurrentSlot];
          var toolSlotOffsetPosition = new Position(toolSlotPosition.X + toolChangeSlotOffset.X, toolSlotPosition.Y + toolChangeSlotOffset.Y);

          if (!ExecuteGCode(
              // move to safe z
              "G00 G53 Z" + zSafe,
              // move to tool slot offset position
              "G00 G53 X" + toolSlotOffsetPosition.X + " Y" + toolSlotOffsetPosition.Y,
              // move to tool slot position (if top-unload)
              (toolSlotIsSideLoad ? "" : "G00 G53 X" + toolSlotPosition.X + " Y" + toolSlotPosition.Y),
              // move Z axis to tool release position, with offset if not side-load
              "G00 G53 Z" + (toolSlotIsSideLoad ? toolChangeReleaseZ : Math.Max(toolChangeReleaseZ + toolChangeOffsetZ, toolChangeReleaseZ)),
              // move Z axis to tool release position (if top-load)
              (toolSlotIsSideLoad ? "" : "G01 G53 F" + toolChangeFeedRate + " Z" + toolChangeReleaseZ),
              // move to tool slot position (if side-unload)
              (toolSlotIsSideLoad ? "G01 G53 F" + toolChangeFeedRate + " X" + toolSlotPosition.X + " Y" + toolSlotPosition.Y : "")
          )) {
            exec.AddStatusmessage("M6: tool change interrupted");
            return;
          }

          // eject tool (3 attempts)
          var counter = 0;
          while (counter < 3 && (counter < 1 || !AssertClampPosition(atcLEDToolClamp, false, atcLEDToolRelease, true))) {
            counter++;
            if (counter > 1) {
              exec.AddStatusmessage("M6: retrying to eject T" + toolCurrentNumber + ", attempt #" + counter);
              ATCClose(atcOutputToolRelease, atcOutputToolClamp, atcOutputPurge, atcToolClampOffDelayMS);
            }

            ATCOpen(atcOutputToolRelease, atcOutputToolClamp, atcOutputPurge);
          }

          // check if tool released
          if (!AssertClampPosition(atcLEDToolClamp, false, atcLEDToolRelease, true)) {
            ATCClose(atcOutputToolRelease, atcOutputToolClamp, atcOutputPurge, atcToolClampOffDelayMS);

            exec.Callbutton(buttonInterrupt);
            exec.AddStatusmessage("M6: failed to eject T" + toolCurrentNumber);
            Prompt("M6: Tool Change Failed", "Failed to eject T" + toolCurrentNumber + "\n" + toolNewDesc, "OK", PromptStatus.Error);
            return;
          }

          // move Z axis up
          if (!ExecuteGCode(
              "G01 G53 F" + toolChangeFeedRate + " Z" + Math.Min(toolChangeReleaseZ + toolChangeOffsetZ, zSafe))
          ) {
            exec.AddStatusmessage("M6: tool change interrupted");
            return;
          }

          ATCClose(atcOutputToolRelease, atcOutputToolClamp, atcOutputPurge, atcToolClampOffDelayMS);

          // check that nothing is spindle
          if (!AssertClampPosition(atcLEDToolClamp, false, atcLEDToolRelease, false)) {
            exec.Callbutton(buttonInterrupt);
            exec.AddStatusmessage("M6: failed to eject T" + toolCurrentNumber + " or close drawbar");
            Prompt("M6: Tool Change Failed", "Failed to eject tool or close drawbar", "OK", PromptStatus.Error);
            return;
          }

          if (!ExecuteGCode(
              // move back to safe Z
              "G00 G53 Z" + zSafe,
              // move to tool slot offset position (prevents traveling over tool slots)
              "G00 G53 X" + toolSlotOffsetPosition.X + " Y" + toolSlotOffsetPosition.Y
          )) {
            exec.AddStatusmessage("M6: tool change interrupted");
            return;
          }
        }
      }
    }

    // nothing should be in spindle at this point - allow being in release position
    if (!AssertClampPosition(atcLEDToolClamp, false, atcLEDToolRelease)) {
      exec.Callbutton(buttonInterrupt);
      exec.AddStatusmessage("M6: spindle not empty or drawbar ajar");
      Prompt("M6: Tool Change Failed", "Spindle not empty or drawbar ajar", "OK", PromptStatus.Error);
      return;
    }

    if (!exec.Ismacrostopped()) {
      exec.Setcurrenttool(0);
      // reset tool offset
      if (!ExecuteGCode("G49")) {
        exec.AddStatusmessage("M6: tool change interrupted");
        return;
      }
    } else {
      exec.AddStatusmessage("M6: tool change interrupted");
      return;
    }

    // if new tool is not in a slot
    if (toolNewIsManual) {
      if (!ExecuteManualToolChange("T" + toolNewNumber + " not available, manual tool change required", ToolChangeAction.Load, toolNewNumber, toolNewDesc)) {
        return;
      }
    } else if (toolNewNumber > 0) {
      // load/clamp tool via ATC

      exec.AddStatusmessage("M6: loading T" + toolNewNumber + " from slot #" + toolNewSlot);

      var toolSlotPosition = toolSlots[toolNewSlot];
      var toolSlotOffsetPosition = new Position(toolSlotPosition.X + toolChangeSlotOffset.X, toolSlotPosition.Y + toolChangeSlotOffset.Y);

      if (!ExecuteGCode(
          // execute any post-macro
          toolChangePostMacro,
          // move to safe z
          "G00 G53 Z" + zSafe,
          // move to tool slot offset position
          "G00 G53 X" + toolSlotOffsetPosition.X + " Y" + toolSlotOffsetPosition.Y,
          // move to tool slot position
          "G00 G53 X" + toolSlotPosition.X + " Y" + toolSlotPosition.Y,
          // move Z axis to tool clamp position with offset
          "G00 G53 Z" + Math.Max(toolChangeClampZ + toolChangeOffsetZ, toolChangeClampZ)
      )) {
        exec.AddStatusmessage("M6: tool change interrupted");
        return;
      }

      // open clamp
      ATCOpen(atcOutputToolRelease, atcOutputToolClamp, atcOutputPurge);

      // check that clamp opened
      if (!AssertClampPosition(atcLEDToolClamp, false, atcLEDToolRelease, true)) {
        ATCClose(atcOutputToolRelease, atcOutputToolClamp, atcOutputPurge, atcToolClampOffDelayMS);
        exec.Callbutton(buttonInterrupt);
        exec.AddStatusmessage("M6: failed to open drawbar for T" + toolNewNumber);
        Prompt("M6: Tool Change Failed", "Failed to open drawbar for T" + toolNewNumber + "\n" + toolNewDesc, "OK", PromptStatus.Error);
        return;
      }

      // move Z axis down to tool clamp position
      if (!ExecuteGCode(
          // move Z axis to tool clamp position
          "G01 G53 F" + toolChangeFeedRate + " Z" + toolChangeClampZ
      )) {
        exec.AddStatusmessage("M6: tool change interrupted");
        // Movement was interrupted, so might not be safe to close automatically
        Prompt("M6: Tool Change Interrupted", "Manual closure of ATC is required", "OK", PromptStatus.Error);
        return;
      }

      // close air and clamp
      ATCClose(atcOutputToolRelease, atcOutputToolClamp, atcOutputPurge, atcToolClampOffDelayMS);

      // check tool is clamped
      if (!AssertClampPosition(atcLEDToolClamp, true, atcLEDToolRelease, false)) {
        exec.Callbutton(buttonInterrupt);
        exec.AddStatusmessage("M6: failed to close drawbar and clamp T" + toolNewNumber);
        Prompt("M6: Tool Change Failed", "Failed to close drawbar and clamp T" + toolNewNumber + "\n" + toolNewDesc, "OK", PromptStatus.Error);
        return;
      }

      // remove tool from slot
      if (!ExecuteGCode(
          // if side-load, move to tool slot offset position
          (toolSlotIsSideLoad ? "G01 G53 F" + toolChangeFeedRate + " X" + toolSlotOffsetPosition.X + " Y" + toolSlotOffsetPosition.Y : ""),
          // if top-load, move to tool clamp position with offset
          (toolSlotIsSideLoad ? "" : "G00 G53 Z" + Math.Max(toolChangeClampZ + toolChangeOffsetZ, toolChangeClampZ))
      )) {
        exec.AddStatusmessage("M6: tool change interrupted");
        return;
      }

      // check (again) tool is clamped
      if (!AssertClampPosition(atcLEDToolClamp, true, atcLEDToolRelease, false)) {
        exec.Callbutton(buttonInterrupt);
        exec.AddStatusmessage("M6: failed to close drawbar and clamp T" + toolNewNumber);
        Prompt("M6: Tool Change Failed", "Failed to close drawbar and clamp T" + toolNewNumber + "\n" + toolNewDesc, "OK", PromptStatus.Error);
        return;
      }

      if (!ExecuteGCode(
         // move back to safe Z
         "G00 G53 Z" + zSafe,
         // move to tool slot offset position (prevents traveling over tool slots)
         "G00 G53 X" + toolSlotOffsetPosition.X + " Y" + toolSlotOffsetPosition.Y
     )) {
        exec.AddStatusmessage("M6: tool change interrupted");
        return;
      }
    }

    // check if nothing is clamped in spindle
    if (toolNewNumber > 0 && !AssertClampPosition(atcLEDToolClamp, true, atcLEDToolRelease, false)) {
      exec.Callbutton(buttonInterrupt);
      exec.AddStatusmessage("M6: T" + toolNewNumber + " not in spindle or drawbar ajar");
      Prompt("M6: Tool Change Failed", "T" + toolNewNumber + " not in spindle or drawbar ajar\n" + toolNewDesc, "OK", PromptStatus.Error);
      return;
    }

    if (!exec.Ismacrostopped()) {

      exec.Setcurrenttool(toolNewNumber);
      // apply tool offset if enabled
      if (applyToolOffset && !ExecuteGCode("G43 H" + toolNewNumber)) {
        exec.AddStatusmessage("M6: tool change interrupted");
        return;
      }

      // execute tool offset probe if enabled
      if (toolNewNumber > 0 && toolOffsetProbeAfterToolChange) {
        var macro = toolOffsetProbeMacro + " H" + toolNewNumber;
        exec.AddStatusmessage("M6: executing tool offset probe " + macro);
        if (!ExecuteGCode(macro)) {
          exec.AddStatusmessage("M6: tool change interrupted");
          return;
        }
      }

      if (!ExecuteGCode(
          // move to safe z
          "G00 G53 Z" + zSafe,
          // move back to original XY position if enabled
          (restoreXYPosition ? "G00 G53 X" + originalPosition.X + " Y" + originalPosition.Y : ""),
          // restore modal
          String.Join(" ", Array.FindAll(originalModal,
              // if applyToolOffset, filter out original G43/G49
              modal => !applyToolOffset || (!modal.StartsWith("G43") && modal != "G49")
          ))
      )) {
        exec.AddStatusmessage("M6: tool change interrupted");
        return;
      }

    } else {
      ATCClose(atcOutputToolRelease, atcOutputToolClamp, atcOutputPurge, atcToolClampOffDelayMS);
      exec.AddStatusmessage("M6: tool change interrupted");
      return;
    }

#if DEBUG_CSX
  }
#endif
  //#Events
  // ### GLOBAL UTILS ###

  private bool ExecuteGCode(params string[] lines) {
    if (exec.Ismacrostopped() || exec.GetLED(25)) {
      return false;
    }

    var gcode = new List<string>(lines);
    // gcode.ForEach(line =>  exec.AddStatusmessage(line));
    exec.Codelist(gcode);
    while (exec.IsMoving()) { }

    var result = !exec.Ismacrostopped() && !exec.GetLED(25); // !STOP && !RESET
    return result;
  }

  private string FormatD(double? num) {
    return num != null ? String.Format("{0:0.0###}", num.Value) : "<null>";
  }

  private DialogResult Prompt(string title, string messsage, string button, PromptStatus status = PromptStatus.None) {
    var result = exec.Informplugin("Messages.dll",
    string.Format("{0}{1}:{2}|{3}",
        status == PromptStatus.Error ? "#" : status == PromptStatus.Warning ? "!" : "",
        button, title, messsage
    ));
    return result is DialogResult ? (DialogResult)result : DialogResult.None;
  }

  private void ATCOpen(PortPin outputRelease, PortPin? outputClamp, PortPin outputAir, int airOffDelayMS = 0) {
    if (outputClamp != null) {
      exec.Clroutpin(outputClamp.Value.Port, outputClamp.Value.Pin);
      Thread.Sleep(250);
    }

    exec.Setoutpin(outputRelease.Port, outputRelease.Pin);
    Thread.Sleep(250);

    exec.Setoutpin(outputAir.Port, outputAir.Pin);
    Thread.Sleep(250);

    if (airOffDelayMS > 0) {
      // turn air off after delay
      Thread.Sleep(airOffDelayMS);
      exec.Clroutpin(outputAir.Port, outputAir.Pin);
    }
  }

  private void ATCClose(PortPin outputRelease, PortPin? outputClamp, PortPin outputAir, int clampOffDelayMS = 0) {
    exec.Clroutpin(outputAir.Port, outputAir.Pin);
    Thread.Sleep(250);

    exec.Clroutpin(outputRelease.Port, outputRelease.Pin);
    Thread.Sleep(250);

    if (outputClamp != null) {
      exec.Setoutpin(outputClamp.Value.Port, outputClamp.Value.Pin);
      Thread.Sleep(250);

      if (clampOffDelayMS > 0) {
        // turn tool clamp off after delay
        Thread.Sleep(clampOffDelayMS);
        exec.Clroutpin(outputClamp.Value.Port, outputClamp.Value.Pin);
      }
    }
  }

  private bool AssertClampPosition(int clampLed, bool clampExpected, int releaseLed, bool? releaseExpected = null) {
    var retry = 4;
    var debounce = 2;
    var result = false;

    do {
      // dwell after first
      if (debounce < 2 || retry < 4) { Thread.Sleep(250); }

      var clampState = exec.GetLED(clampLed);
      var releaseState = exec.GetLED(releaseLed);

      result = clampState == clampExpected && (releaseExpected == null || releaseState == releaseExpected.Value);
      debounce = result ? debounce - 1 : 2;
      retry = result ? retry : retry - 1;
    } while (retry > 0 && debounce > 0 && !result);

    return result;
  }

  private double GetOffsetZ(int offsetNumber) {
    var fieldStartIdxToolZ_1 = 195; // 196-215 - T1-20
    var fieldStartIdxToolZ_2 = 900; // 921-996 - T21-96
    var fieldStartIdxToolZ = offsetNumber < 21 ? fieldStartIdxToolZ_1 : fieldStartIdxToolZ_2;
    var offsetCurrentZ = offsetNumber > 0 ? double.Parse(AS3.Getfield(fieldStartIdxToolZ + offsetNumber)) : 0.0D;

    return offsetCurrentZ;
  }

  private enum PromptStatus {
    Error = -1,
    None = 0,
    Warning = 1
  }

  private enum ToolChangeAction {
    Load,
    Unload
  }

  delegate bool QTCDelegate(string status, ToolChangeAction action, int toolNumber, string toolDesc, bool skipClamp = false);

  private struct Position {
    public Position(double x, double y) {
      this.X = x;
      this.Y = y;
    }

    public double X;
    public double Y;
  }

  private struct PortPin {
    public PortPin(int port, int pin) {
      this.Port = port;
      this.Pin = pin;
    }

    public int Port;
    public int Pin;
  }

#if DEBUG_CSX
}
#endif
