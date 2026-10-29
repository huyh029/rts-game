# Investigation Report: UI/Input/Networking/Lua Removal for Pure Bot-vs-Bot Engine

## Summary

To transform RecoilEngine into a pure bot-vs-bot simulation engine, the following components must be removed or modified:

### Files to DELETE entirely (29 files):
- **Lua Rules**: LuaRules.cpp/h, LuaRulesParams.cpp/h (4 files)
- **Lua UI**: LuaUI.cpp/h, LuaUICommand.cpp/h, LuaInputReceiver.cpp/h (4 files)  
- **Lua Gaia**: LuaGaia.cpp/h (2 files)
- **Lua Menu**: LuaMenu.cpp/h, LuaMenuController.cpp/h (2 files)
- **aGUI**: entire aGui/ directory (10 files: Button.cpp/h, Gui.cpp/h, GuiElement.cpp/h, HorizontalLayout.cpp/h, LineEdit.cpp/h, List.cpp/h, Picture.cpp/h, TextElement.cpp/h, VerticalLayout.cpp/h, Window.cpp/h)
- **Menu**: Menu/ directory (5 files: LuaMenuController.cpp/h, SelectMenu.cpp/h, SelectionWidget.cpp/h, SelectionWidget.h, UpdaterWindow.h)
- **Game UI**: Game/UI/ directory (20 files: CommandColors.cpp/h, CursorIcons.cpp/h, EndGameBox.cpp/h, GameInfo.cpp/h, GameSetupDrawer.cpp/h, GuiHandler.cpp/h, HwMouseCursor.cpp/h, IKeys.cpp/h, InfoConsole.cpp/h, InputReceiver.cpp/h, KeyBindings.cpp/h, KeyCodes.cpp/h, KeySet.cpp/h, MiniMap.cpp/h, MouseCursor.cpp/h, MouseHandler.cpp/h, PlayerRoster.cpp/h, PlayerRosterDrawer.cpp/h, ProfileDrawer.cpp/h, QuitBox.cpp/h, ResourceBar.cpp/h, ScanCodes.cpp/h, SelectionKeyHandler.cpp/h, ShareBox.cpp/h, StartPosSelecter.cpp/h, TooltipConsole.cpp/h, UnitTracker.cpp/h)

### Files to MODIFY significantly (15 files):

#### Core Game Files:
1. **PreGame.cpp/h** - Remove PreGame class entirely or stub to minimal server-only version
   - Remove all networking code (clientNet, connection handling, packet processing)
   - Remove menu activation code (CLuaMenuController::ActivateInstance calls)
   - Remove demo file handling
   - Keep only CPreGame::GameDataReceived and minimal server initialization logic

2. **GameInputReceiver.cpp/h** - Remove user input handling
   - Remove keyboard/mouse input processing
   - Keep only Action-based command processing from bots/AI
   - Remove RmlGui, LuaInputReceiver, gameTextInput integration
   - Stub to process only bot-generated actions

3. **GlobalUnsynced.cpp/h** - Keep basic data structure but remove unsynced UI state
   - Keep: myPlayerNum, myTeam, myAllyTeam (needed for AI identity)
   - Remove: globalQuit, globalReload (no user interaction)
   - Remove: fpsMode, spectating* (UI concepts)
   - Keep: gameTime, modGameTime, startTime (simulation state)

4. **Game.cpp** - Major refactoring needed
   - Remove Lua loading (LoadLua, KillLua)
   - Remove UI loading (LoadInterface, KillInterface)
   - Remove aGui initialization
   - Remove RmlGui initialization/shutdown
   - Remove video capturing
   - Remove chat message handling
   - Keep: simulation loop, AI loading, game state management
   - Remove: Draw(), DrawInputReceivers(), DrawInputText(), DrawInterfaceWidgets()
   - Keep: Update(), SimFrame(), ActionReceived() for bot commands

5. **luaInputReceiver** (LuaInputReceiver.cpp/h) - Remove entirely
   - This is the bridge between user input and Lua
   - All functionality can be removed

6. **LuaMenu.cpp/h** (if exists) - Remove entirely
   - Menu system code

#### Networking Files (to DELETE - keep only stubs if needed):
7. **Net/ directory** - Most networking code can be removed
   - NetProtocol.cpp/h - Keep only local connection stubs if needed for bot communication
   - GameServer.cpp/h - Keep server logic but remove network sockets
   - LocalConnection.cpp/h, LoopbackConnection.cpp/h - Keep as stubs for local communication
   - UDPConnection.cpp/h, UDPListener.cpp/h - Remove entirely
   - PackPacket.cpp/h, UnpackPacket.cpp/h - Remove or stub
   - RawPacket.cpp/h - Remove or stub
   - All other net files in rts/System/Net/ - Remove

#### External AI Files (KEEP - these are AI wrappers):
8. **ExternalAI/** - Keep all files for AI wrapper functionality
   - SkirmishAIWrapper.cpp/h - Keep
   - AILibraryManager.cpp/h - Keep
   - SkirmishAIHandler.cpp/h - Keep
   - SkirmishAILibrary.cpp/h - Keep
   - All other AI interface files - Keep

### Files to MODIFY minimally (10 files):

9. **KeyBindings.cpp/h** - Remove key-to-action mapping
   - Keep ActionList and Action structures
   - Remove key binding loading/saving
   - Stub to return empty action lists for keyboard input
   - Keep ExecuteCommand() for script-based command input

10. **MouseHandler.cpp/h** - Remove mouse cursor and movement
    - Keep mouse position tracking if needed for AI
    - Remove cursor drawing, cursor changes, mouse wheel
    - Stub all mouse methods to return false/empty

11. **MouseCursor.cpp/h** - Remove cursor rendering
    - Remove cursor icon loading/drawing
    - Keep only cursor position tracking if needed

12. **CursorIcons.cpp/h** - Remove cursor icons
    - Remove all cursor icon rendering code
    - Keep only data structures if AI needs them

13. **KeyCodes.cpp/h, ScanCodes.cpp/h, KeySet.cpp/h, KeyBindings.cpp/h** - Remove keyboard mappings
    - Keep action structures
    - Remove key-to-action binding logic
    - Stub to return empty lists

14. **MiniMap.cpp/h** - Remove minimap rendering
    - Keep only if AI uses minimap data
    - Remove drawing code
    - Keep minimap position/state if AI needs it

15. **InfoConsole.cpp/h** - Keep only basic logging
    - Remove GUI display
    - Keep console text storage
    - Remove key handling

16. **TooltipConsole.cpp** - Remove entirely
    - UI-only feature

17. **UnitTracker.cpp** - Remove entirely
    - UI-only feature for tracking selected units

18. **EndGameBox.cpp/h, QuitBox.cpp/h, ShareBox.cpp/h, GameSetupDrawer.cpp/h** - Remove entirely
    - UI dialog boxes

### Files that can stay as-is (25+ files):

19. **Simulation files** (Sim/ directory) - Keep entire contents
    - Unit handling, AI, physics, etc.

20. **Map files** (Map/ directory) - Keep entire contents
    - Map data loading and parsing

21. **Rendering core** (some Rendering/ files) - Keep minimal headless rendering
    - WorldDrawer.cpp/h - Keep for AI state observation (if needed)
    - Remove: HUDDrawer.cpp/h (UI overlay)
    - Remove: LuaObjectDrawer.cpp/h (Lua UI rendering)
    - Remove: CommandDrawer.cpp/h (UI commands)
    - Keep: Model rendering for headless observation if needed

22. **Game controller base** (GameController.cpp/h) - Keep base classes
    - Keep CGameController, CGameControllerTextInput
    - Remove UI-specific subclasses

23. **Lua library files** (rts/Lua/library/) - Keep only if needed for AI
    - Spring.lua, Types.lua, Script.lua
    - These contain AI APIs, not UI

24. **Action.cpp/h, Action.h** - Keep action structures
    - Bot commands use these

25. **ConfigHandler** - Keep configuration system
    - Remove UI-specific config options

## Key Dependencies and Breaking Changes

### Critical Dependencies to Address:

1. **LuaRules, LuaUI, LuaGaia dependencies**:
   - These files import each other (LuaRules includes LuaUI, etc.)
   - Removing one requires removing all three
   - All three load Lua code from LuaRules/, LuaUI/, LuaGaia/ directories
   - **Impact**: AI that uses Lua for scripting will break

2. **GameInputReceiver dependencies**:
   - Includes Game/UI/InputReceiver.h and Game/UI/KeySet.h
   - Depends on luaInputReceiver, luaUI, luaMenu
   - **Impact**: Bot command input will break

3. **Game.cpp dependencies**:
   - Includes 100+ files including all UI, Lua, and networking
   - **Impact**: Game startup will break unless heavily refactored

4. **PreGame dependencies**:
   - Includes Net/NetProtocol.h, Menu/LuaMenuController.h, aGui/Gui.h
   - **Impact**: Game initialization will break

5. **GlobalUnsynced dependencies**:
   - Referenced by many files for myPlayerNum, myTeam, gameTime
   - **Impact**: Minimal - only need to keep basic structure

## Recommended Approach

### Phase 1: Remove Lua scripting (highest impact)
1. Remove LuaRules, LuaUI, LuaGaia completely
2. Remove LuaMenu, LuaMenuController
3. Remove LuaInputReceiver
4. Remove all Lua loading from Game.cpp
5. Stub all Lua global pointers (luaRules, luaUI, luaGaia, luaMenu)

### Phase 2: Remove UI rendering
1. Remove aGui directory
2. Remove Menu directory  
3. Remove Game/UI directory
4. Remove HUDDrawer, LuaObjectDrawer, CommandDrawer from Rendering
5. Stub all UI rendering calls in Game.cpp

### Phase 3: Remove user input
1. Remove GameInputReceiver.cpp/h
2. Remove MouseHandler, MouseCursor, CursorIcons
3. Remove KeyBindings, KeyCodes, KeySet
4. Stub keyboard/mouse input handling in Game.cpp

### Phase 4: Remove networking (or stub for local only)
1. Remove NetProtocol, GameServer networking code
2. Keep LocalConnection, LoopbackConnection for local bot communication
3. Remove UDP, socket code
4. Stub all network-related global variables

### Phase 5: Remove menu system
1. Remove PreGame.cpp/h or convert to minimal server-only
2. Remove SelectMenu, SelectionWidget
3. Stub menu activation calls

## Minimal Bot-vs-Bot Engine Requirements

To keep a functional bot-vs-bot engine:

1. **Keep in Game.cpp**:
   - Load map and game definitions
   - Load AI libraries (ExternalAI/)
   - SimFrame() simulation loop
   - ActionReceived() for bot commands
   - Save/load game state (if needed)

2. **Keep in Sim/ directory**:
   - All simulation logic (units, AI, physics, etc.)

3. **Keep in Map/ directory**:
   - Map loading and parsing

4. **Keep minimal Lua**:
   - Spring.lua, Types.lua if AI uses them
   - Remove LuaRules, LuaUI, LuaGaia entirely

5. **Keep minimal rendering** (optional):
   - WorldDrawer if AI needs visual state
   - Remove all UI rendering

6. **Keep networking stubs** (optional):
   - LocalConnection only if bots communicate
   - Remove all socket code

## Estimated Removal Impact

- **Lines of code removed**: ~15,000-20,000
- **Files deleted**: ~70-80
- **Files modified**: ~15-20
- **AI compatibility**: May break Lua-based AI, C++ AI should work
- **Build complexity**: Reduced significantly
- **Runtime memory**: Reduced significantly (no UI textures, fonts, etc.)

## Next Steps

1. Create a headless build configuration
2. Remove LuaRules, LuaUI, LuaGaia first (highest impact)
3. Stub all global pointers to prevent crashes
4. Test with simple bot-vs-bot match
5. Iterate on remaining dependencies
6. Remove UI, input, networking in stages
7. Add minimal UI for debugging if needed (headless mode)