# Technical Design: Pure Bot-vs-Bot Simulation Engine

## Overview

This design documents the removal of all UI, keyboard, mouse, and user input code from RecoilEngine to create a pure bot-vs-bot simulation engine. The engine will retain full simulation capabilities (AI logic, unit control, physics, etc.) but eliminate all user-facing interfaces. External AI bots will control the game through the Action system rather than through keyboard/mouse input.

## Technology Stack

**Language**: C++17  
**Build System**: CMake  
**External Dependencies**: No UI/graphics dependencies required for headless build  
**Target Platform**: Windows/Linux (cross-platform)

## Core Architecture Changes

### 1. Build Configuration

Add a new build flag `HEADLESS` (already used in codebase) to conditionally compile UI components. The existing `HEADLESS` preprocessor macro will be extended to:

1. Exclude UI source files from compilation
2. Stub out UI-related function calls
3. Remove graphics rendering calls while keeping simulation logic

### 2. Directory/File Removal Strategy

The following directories and files will be completely removed:

| Category | Files | Total Files |
|----------|-------|-------------|
| Lua Rules | LuaRules.cpp/h, LuaRulesParams.cpp/h | 4 |
| Lua UI | LuaUI.cpp/h, LuaUICommand.cpp/h, LuaInputReceiver.cpp/h | 4 |
| Lua Gaia | LuaGaia.cpp/h | 2 |
| Lua Menu | LuaMenu.cpp/h, LuaMenuController.cpp/h | 2 |
| aGui | All 10 files in rts/aGui/ | 10 |
| Menu | All 5 files in rts/Menu/ | 5 |
| Game/UI | All 20 files in rts/Game/UI/ | 20 |

**Total files to remove: 47**

### 3. Core File Modifications

#### PreGame.cpp/h

**Current role**: Manages game startup, networking, and client/server initialization  
**After changes**: Minimal server initialization stub

**Changes required**:
- Remove includes: `Net/GameServer.h`, `Net/Protocol/NetProtocol.h`, `Menu/LuaMenuController.h`, `aGui/Gui.h`
- Remove `CNetProtocol` member variable
- Remove `UpdateClientNet()` method and networking code
- Remove `CLuaMenuController::ActivateInstance()` calls
- Remove demo file loading code (demo recording/playback)
- Stub `Draw()` method to empty implementation (returns `false`)
- Keep: `CPreGame(std::shared_ptr<ClientSetup>)` constructor for server initialization
- Keep: `GameDataReceived()` for game setup data handling
- Remove: All `Draw()` implementations for UI rendering
- Remove: `KeyPressed()` for ESC key handling

**Rationale**: PreGame's primary role is to establish connection and load game data. After removal, it only needs to initialize the server and transition to CGame.

#### GameInputReceiver.cpp/h

**Current role**: Processes keyboard/mouse input and translates to Actions  
**After changes**: Stub to process only Action commands from bots

**Changes required**:
- Remove includes: `Game/UI/KeyBindings.h`, `Game/UI/KeyCodes.h`, `Game/UI/KeySet.h`, `Game/UI/ScanCodes.h`, `Lua/LuaInputReceiver.h`, `Lua/LuaMenu.h`, `Lua/LuaUI.h`, `<Rml/Backends/RmlUi_Backend.h>`
- Remove: `CTimedKeyChain` members (`curKeyCodeChain`, `curScanCodeChain`)
- Remove: `RmlGui::ProcessKeyPressed/Released()` calls
- Remove: `gameTextInput.ConsumePressed/ReleasedKey()` calls
- Remove: `luaInputReceiver->KeyPressed/KeyReleased()` calls
- Keep: `CInputReceiver::MANUAL` base class
- Remove: `lastActionList` member (will be replaced by direct Action processing)
- Remove: `KeyReleased()`, `MousePress()`, `MouseRelease()` methods (user input only)
- Keep: `ActionPressed()`, `ActionReleased()` processing for bot commands

**Rationale**: Input receiver's job is to translate user actions into game Actions. For headless mode, we only need to accept Actions directly from bots via the Action system.

#### GlobalUnsynced.cpp/h

**Current role**: Global unsynced state variables  
**After changes**: Keep only essential variables for AI identity

**Changes required**:
- Remove includes: UI-specific headers if any
- Keep members:
  - `myPlayerNum` - AI bot identity
  - `myTeam` - AI team assignment  
  - `myAllyTeam` - AI ally team
  - `modGameTime`, `gameTime`, `startTime` - simulation timing
- Remove members:
  - `globalQuit`, `globalReload` (no user interaction)
  - `fpsMode` (no UI)
  - `spectating*` flags (UI concepts)
- Keep methods:
  - `SetMyPlayer()`, `GetMyPlayer()`
- Keep: `ResetState()` implementation with only essential fields
- Keep: `LoadFromSetup()` for loading AI setup data

**Rationale**: AI bots need to know their player number, team, and game timing, but don't need unsynced UI state.

#### Game.cpp

**Current role**: Main game controller, loads Lua, UI, and manages game loop  
**After changes**: Minimal simulation controller

**Changes required**:

**Remove includes** (approx 100+ lines):
- Lua includes: `Lua/LuaGaia.h`, `Lua/LuaInputReceiver.h`, `Lua/LuaMenu.h`, `Lua/LuaRules.h`, `Lua/LuaUI.h`
- UI includes: All `Game/UI/*.h`, `UI/*.h`, `Rml/*.h`
- Rendering UI: `Rendering/HUDDrawer.h`, `Rendering/CommandDrawer.h`, `Rendering/Icons/*.h`
- Net includes: `Net/GameServer.h`, `Net/Protocol/NetProtocol.h`
- Menu includes: `Menu/*.h`
- aGui includes: `aGui/*.h`

**Keep includes**:
- Simulation: `Sim/*.h`
- Map: `Map/*.h`
- External AI: `ExternalAI/*.h`
- Game core: `Game/Players/*.h`, `Game/Action.h`, `Game/GameHelper.h`

**Remove methods**:
- `LoadLua()` - Remove entire method
- `KillLua()` - Stub to empty implementation
- `LoadInterface()` - Remove entire method
- `KillInterface()` - Remove entire method
- `LoadSkirmishAIs()` - Keep AI loading but remove Lua AI loading
- `Draw()`, `DrawInputReceivers()`, `DrawInputText()`, `DrawInterfaceWidgets()` - Stub to empty
- `SendNetChat()` - Remove networking chat
- `ProcessCommandText()` - Remove command line processing
- `ReloadCOB()`, `ReloadCEGs()` - Remove Lua reload functionality

**Keep methods**:
- `CGame()` constructor - Keep, but remove RmlGui initialization
- `~CGame()` destructor - Remove RmlGui shutdown, Lua cleanup
- `Update()`, `SimFrame()` - Keep simulation loop
- `ActionReceived()`, `ActionPressed()`, `ActionReleased()` - Keep bot command processing
- `Load()`, `LoadMap()`, `LoadDefs()`, `PreLoadSimulation()`, `PostLoadSimulation()` - Keep map/AI loading
- `GameEnd()` - Keep end-game state management
- `StartSkip()`, `EndSkip()` - Keep if needed for AI training

**Constructor changes**:
- Remove: `RmlGui::Shutdown()` call in destructor
- Remove: `KillLua(true)` call in destructor
- Remove: `KillInterface()` call in destructor
- Remove: `loadDone`, `gameOver` atomics if only used for UI
- Remove: `hideInterface` member (no UI)
- Remove: `gameDrawMode` (no rendering)
- Remove: `showFPS`, `showClock`, `showSpeed` (no UI display)
- Remove: `chatSound` (no chat)
- Remove: `playerTraffic` map (no networking stats)

**Rationale**: Game.cpp is the central hub. We must remove all Lua/UI loading but keep the simulation loop and Action processing for bots.

#### KeyBindings.cpp/h

**Current role**: Maps keyboard keys to game Actions  
**After changes**: Stub to return empty action lists

**Changes required**:
- Remove includes: `Game/UI/KeyCodes.h`, `Game/UI/KeySet.h`
- Remove: `keyToActionMap` data structure
- Remove: `LoadKeyConfig()`, `SaveKeyConfig()` methods
- Remove: `GetActionList()` methods that process key chains
- Keep: `ExecuteCommand()` for script-based command execution (used by AI)
- Remove: `CKeyBindings` class entirely (or stub to empty)

**Rationale**: Key bindings are purely for user input. AI uses direct Action commands, not keyboard mappings.

#### MouseHandler.cpp/h, MouseCursor.cpp/h, CursorIcons.cpp/h

**Current role**: Mouse cursor rendering and movement  
**After changes**: Remove entirely

**Changes required**:
- Remove all three files from Game/UI/ directory
- Remove includes from all files that reference these
- Stub any calls to these classes to no-ops

**Rationale**: No cursor needed in headless mode.

#### InfoConsole.cpp/h, TooltipConsole.cpp, UnitTracker.cpp

**Current role**: UI console/tooltip/unit tracking  
**After changes**: Remove entirely

**Changes required**:
- Remove all three files
- Remove includes from all files
- Keep basic logging infrastructure (separate from UI console)

**Rationale**: Pure UI components with no simulation value.

#### EndGameBox.cpp/h, QuitBox.cpp/h, ShareBox.cpp/h, GameSetupDrawer.cpp/h

**Current role**: UI dialog boxes  
**After changes**: Remove entirely

**Changes required**:
- Remove all four files
- Remove includes
- Stub any calls to these classes to no-ops

**Rationale**: No user interaction dialog boxes in headless mode.

### 4. Networking Changes

#### Net/Directory

**Files to remove**:
- `Net/Protocol/NetProtocol.cpp/h` - Client networking
- `Net/GameServer.cpp/h` - Server networking (keep server logic only)
- `Net/NetCommands.cpp` - Network command serialization
- `Net/AutohostInterface.cpp` - Autohost communication

**Files to keep**:
- `Net/BaseNetProtocol.cpp/h` - Base class (may need stubbing)
- `Net/LocalConnection.cpp/h` - Local communication stub
- `Net/LoopbackConnection.cpp/h` - Loopback for local bot communication

**Changes required**:
- Remove socket programming code
- Remove UDP connection handling
- Remove network packet serialization
- Keep only local message passing if needed for bot-to-bot communication
- Stub `CNetProtocol` to no-op for client connections
- Remove `CGameServer` networking code, keep server state management

**Rationale**: Bot-vs-bot doesn't need network communication unless bots are on different processes. Even then, local IPC is sufficient.

#### PreGame.cpp Changes

**Remove includes**:
- `Net/GameServer.h`, `Net/Protocol/NetProtocol.h`

**Remove**:
- `clientNet` member variable
- `InitClient()`, `InitLocalClient()` methods
- `CheckTimeout()`, `Connected()` methods
- `PreSimFrame()`, `HasStarted()` methods related to networking

**Keep**:
- `StartServer()` for local simulation
- `GameDataReceived()` for setup data

**Rationale**: PreGame only needs to set up local simulation, not network connections.

### 5. Lua Changes

**Files to remove**:
- `Lua/LuaRules.cpp/h` - Rules scripting
- `Lua/LuaRulesParams.cpp/h` - Rules parameters
- `Lua/LuaUI.cpp/h` - UI scripting
- `Lua/LuaUICommand.cpp/h` - UI commands
- `Lua/LuaInputReceiver.cpp/h` - Input bridge
- `Lua/LuaGaia.cpp/h` - Gaia scripting
- `Lua/LuaMenu.cpp/h` - Menu scripting
- `Lua/LuaMenuController.cpp/h` - Menu controller

**Files to keep**:
- `Lua/LuaHandle.cpp/h` - Lua state management (keep minimal)
- `Lua/LuaParser.cpp/h` - Script parsing (for AI scripts if needed)
- `Lua/LuaUtils.cpp` - Utility functions
- `Lua/library/*.lua` - Lua standard library (Spring.lua, Types.lua, etc.)

**Changes required**:
- Remove all Lua state initialization
- Remove all Lua global variables (`luaRules`, `luaUI`, `luaGaia`, `luaMenu`)
- Remove `LoadLua()` calls from Game.cpp
- Stub `LuaHandle::CreateInstance()` to no-op
- Keep minimal Lua for AI script execution if AI uses it

**Rationale**: LuaRules, LuaUI, and LuaGaia are UI/scripting layers over simulation. AI bots use direct C++ APIs, not Lua scripts.

### 6. External AI (KEEP)

**Files to keep**:
- `ExternalAI/SkirmishAIHandler.cpp/h` - AI handler
- `ExternalAI/SkirmishAILibrary.cpp/h` - AI library wrapper
- `ExternalAI/EngineOutHandler.cpp/h` - AI to engine communication
- `ExternalAI/AILibraryManager.cpp/h` - AI library management

**Rationale**: These provide the interface between the engine and AI bots. They MUST be kept for bot functionality.

### 7. Rendering Changes

**Files to keep** (if headless rendering is needed for debugging):
- `Rendering/WorldDrawer.cpp/h` - Main rendering (can be stubbed)
- `Rendering/GlobalRendering.cpp/h` - Rendering context (can be stubbed)

**Files to remove**:
- `Rendering/HUDDrawer.cpp/h` - UI overlay
- `Rendering/CommandDrawer.cpp/h` - UI commands
- `Rendering/LuaObjectDrawer.cpp/h` - Lua rendering
- `Rendering/Icons/*.cpp/h` - Icon rendering

**Rationale**: If the engine is truly headless (no rendering at all), these can be removed. If headless debugging is needed, stub them to no-op.

### 8. Build System Changes

#### CMakeLists.txt Changes

**Remove subdirectories**:
```cmake
# Remove from rts/CMakeLists.txt:
add_subdirectory(aGui)
add_subdirectory(Menu)
```

**Modify Game subdirectory**:
```cmake
# In rts/Game/CMakeLists.txt:
# Conditionally include UI files based on HEADLESS flag

if(NOT HEADLESS)
    list(APPEND sources_engine_Game
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/CommandColors.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/CursorIcons.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/EndGameBox.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/GameInfo.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/GameSetupDrawer.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/GuiHandler.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/Groups/Group.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/Groups/GroupHandler.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/HwMouseCursor.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/InfoConsole.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/InputReceiver.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/KeyBindings.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/KeyCodes.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/ScanCodes.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/IKeys.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/KeySet.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/MiniMap.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/MouseCursor.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/MouseHandler.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/PlayerRoster.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/PlayerRosterDrawer.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/ProfileDrawer.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/QuitBox.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/ResourceBar.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/SelectionKeyHandler.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/ShareBox.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/StartPosSelecter.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/TooltipConsole.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/UI/UnitTracker.cpp"
    )
endif()
```

**Modify Lua subdirectory**:
```cmake
# In rts/Lua/CMakeLists.txt:
# Conditionally include Lua UI files

if(NOT HEADLESS)
    list(APPEND sources_engine_Lua
        "${CMAKE_CURRENT_SOURCE_DIR}/LuaGaia.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/LuaInputReceiver.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/LuaMenu.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/LuaRules.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/LuaRulesParams.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/LuaUI.cpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/LuaUICommand.cpp"
    )
endif()
```

**Remove aGui from engine sources**:
```cmake
# In rts/CMakeLists.txt:
make_global_var(engineSources
    ${sources_engine_Net}
    ${sources_engine_Lua}
    ${sources_engine_Map}
    ${sources_engine_Rendering}
    # ${sources_engine_aGui}  # REMOVED
    # ${sources_engine_Rml}   # REMOVED (Rml is UI framework)
    # ${sources_engine_Menu}  # REMOVED
    ${sources_engine_System}
    ${sources_engine_ExternalAI}
)
```

### 9. Global Header Changes

#### SpringVersion.h

**Keep**:
- `IsHeadless()` method

**Rationale**: Already exists, will be used for conditional compilation.

### 10. Error Handling Strategy

All removed functions should be stubbed to safe defaults:

| Removed Feature | Error Handling |
|-----------------|----------------|
| Networking | Return empty responses, no connection errors |
| User Input | Return false/empty, no action taken |
| UI Drawing | Return false/empty, no rendering |
| Lua Loading | Skip gracefully, log warning if Lua expected |
| Menu Activation | Return false, no menu shown |

### 11. Validation Rules

**Input Validation**:
- `CGame::ActionReceived()`: Validate Action structure, reject malformed actions
- `CPreGame::GameDataReceived()`: Validate game setup data, reject invalid configurations
- AI Bot Identity: Validate player/team indices against setup data

**Invariant Enforcement**:
- AI Bot Count: Enforced by ExternalAI/SkirmishAIHandler
- Game State: Enforced by CGame simulation loop
- Sync: Enforced by Simulation/Ecs/Registry

### 12. Testing Strategy

**Unit Tests**:
- Action processing without input receivers
- AI bot command execution
- Game state initialization
- Map loading without UI

**Integration Tests**:
- Bot-vs-bot match startup
- Game loop execution
- AI library loading
- Action command processing

**Manual Testing**:
- Verify headless build compiles
- Verify no UI dependencies in binary
- Verify bot-vs-bot match runs end-to-end

### 13. Edge Cases

1. **No AI bots**: Engine should start with error message
2. **Invalid setup data**: Engine should log error and exit
3. **Lua scripts expected**: Engine should warn but continue (if possible)
4. **Drawing requested**: Stubbed draw calls should return immediately

### 14. Migration Path

**Phase 1**: Remove Lua rules/UI/Gaia
- Remove LuaRules, LuaUI, LuaGaia
- Stub Lua loading calls
- Test basic bot startup

**Phase 2**: Remove UI directories
- Remove aGui, Menu, Game/UI
- Stub UI rendering calls
- Test headless build

**Phase 3**: Remove input handling
- Remove GameInputReceiver, MouseHandler, KeyBindings
- Stub input processing
- Verify bot commands work

**Phase 4**: Remove networking
- Remove NetProtocol, GameServer networking code
- Keep local connection stubs
- Test local bot-vs-bot

**Phase 5**: Clean up build system
- Update CMakeLists.txt
- Test cross-platform builds

## Files to Modify (15 files)

1. **PreGame.cpp/h** - Remove networking, stub to minimal server init
2. **GameInputReceiver.cpp/h** - Remove user input, keep Action processing
3. **Game.cpp** - Remove Lua/UI loading, keep simulation loop
4. **GlobalUnsynced.cpp/h** - Remove UI state, keep AI identity
5. **luaInputReceiver.cpp/h** - Remove entirely (bridge to Lua)
6. **KeyBindings.cpp/h** - Remove key-to-action mapping
7. **MouseHandler.cpp/h** - Remove mouse cursor
8. **MouseCursor.cpp/h** - Remove cursor rendering
9. **CursorIcons.cpp/h** - Remove cursor icons
10. **InfoConsole.cpp/h** - Keep logging, remove UI
11. **TooltipConsole.cpp/h** - Remove entirely
12. **UnitTracker.cpp/h** - Remove entirely
13. **EndGameBox.cpp/h** - Remove entirely
14. **QuitBox.cpp/h** - Remove entirely
15. **ShareBox.cpp/h** - Remove entirely

## Files to Delete (47 files)

**Lua Rules** (4 files):
- LuaRules.cpp, LuaRules.h
- LuaRulesParams.cpp, LuaRulesParams.h

**Lua UI** (4 files):
- LuaUI.cpp, LuaUI.h
- LuaUICommand.cpp, LuaUICommand.h
- LuaInputReceiver.cpp, LuaInputReceiver.h

**Lua Gaia** (2 files):
- LuaGaia.cpp, LuaGaia.h

**Lua Menu** (2 files):
- LuaMenu.cpp, LuaMenu.h
- LuaMenuController.cpp, LuaMenuController.h

**aGui** (10 files):
- Button.cpp/h, Gui.cpp/h, GuiElement.cpp/h
- HorizontalLayout.cpp/h, LineEdit.cpp/h, List.cpp/h
- Picture.cpp/h, TextElement.cpp/h, VerticalLayout.cpp/h, Window.cpp/h

**Menu** (5 files):
- LuaMenuController.cpp/h, SelectMenu.cpp/h
- SelectionWidget.cpp/h

**Game/UI** (20 files):
- CommandColors.cpp/h, CursorIcons.cpp/h, EndGameBox.cpp/h
- GameInfo.cpp/h, GameSetupDrawer.cpp/h, GuiHandler.cpp/h
- HwMouseCursor.cpp/h, IKeys.cpp/h, InfoConsole.cpp/h
- InputReceiver.cpp/h, KeyBindings.cpp/h, KeyCodes.cpp/h
- ScanCodes.cpp/h, KeySet.cpp/h, MiniMap.cpp/h
- MouseCursor.cpp/h, MouseHandler.cpp/h, PlayerRoster.cpp/h
- PlayerRosterDrawer.cpp/h, ProfileDrawer.cpp/h, QuitBox.cpp/h
- ResourceBar.cpp/h, SelectionKeyHandler.cpp/h, ShareBox.cpp/h
- StartPosSelecter.cpp/h, TooltipConsole.cpp/h, UnitTracker.cpp/h

## Files to Keep

**Simulation**:
- All files in `Sim/` directory
- All files in `Map/` directory
- External AI wrappers in `ExternalAI/`

**Core**:
- All files in `rts/Game/` except UI/input
- Basic rendering core (WorldDrawer if needed)
- Game controller base classes

**Build System**:
- CMakeLists.txt files (modified)
- Build configurations

## Summary

The technical design removes ~75 files (47 direct files + 15 core files modified + 13+ networking files) to create a pure bot-vs-bot simulation engine. The engine retains:

- Full simulation capabilities (units, physics, AI logic)
- External AI bot interface
- Map loading and game definitions
- Action command system for bot control
- Basic logging and error handling

The engine removes:

- All UI rendering (aGui, Menu, Game/UI, HUDDrawer)
- All user input handling (keyboard, mouse, touch)
- All Lua scripting (LuaRules, LuaUI, LuaGaia, LuaMenu)
- Networked multiplayer (NetProtocol, GameServer networking)
- Menu systems (CLuaMenuController, selection widgets)

Bot-vs-bot matches will be initiated by:
1. Loading map and game definitions
2. Loading AI libraries via ExternalAI
3. Processing Actions from bots via ActionReceived()
4. Running simulation loop (Update/SimFrame)

The design aligns with the requirements: pure bot-vs-bot with no user input or UI, retaining only the simulation and AI interface capabilities.