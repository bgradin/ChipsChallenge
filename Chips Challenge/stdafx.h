#pragma once
#include "TargetVer.h"

// Exclude rarely-used stuff from Windows headers
#define WIN32_LEAN_AND_MEAN

// Windows Header Files:
#include <windows.h>
#include <WindowsX.h>

// C RunTime Header Files
#include <stdlib.h>
#include <malloc.h>
#include <memory.h>
#include <tchar.h>

// Program-required additional headers
#include <direct.h>			// Used for _getcwd()
#include <string>
#include <iomanip>			// Used for hex conversion
#include <fstream>
#include <list>
#include <map>
#include <functional>
#include <MMSystem.h>		// Used for sound
#include <queue>
#include <cmath>
#include "boost/thread.hpp"
#include "Resource.h"
#include "BlockTypes.h"
#include "HtmlHelp.h"		// Help file integration
#include "Conversion.h"		// Useful conversion functions
#include "FileIO.h"			// Functions for reading .DAT file
#include "Chips Challenge.h"// Contains global variables and functions
#include "Map.h"			// Map class
#include "Wav.h"			// Wave class - sound effects
#include "Midi.h"			// MIDI class - music
#include "Ini.h"			// INI class - save file
#include "Game.h"			// Player, Monster, Chip, Trap and direction classes