//*****************************************************//
//	     Brian Gradin, 2013			        		   //
//       Chip's challenge -> Chip's Challenge.h        //
//*****************************************************//
// - Contains some global variables and functions
// - All functions are initialized in Chip's Challenge.cpp

#include "stdafx.h"

//
// FUNCTION: ReportError(std::string)
//
// PURPOSE:  Used for error reporting
//
void ReportError(std::string);

//
// FUNCTION: DrawMap()
//
// PURPOSE:  Forces the window to be redrawn
//
void DrawMap();

//
// FUNCTION: DrawLevelNumber(int)
//
// PURPOSE:  Forward declaration to the function called in Chips Challenge.cpp which draws the level number
//
void DrawLevelNumber(int);

//
// FUNCTION: callVictory()
//
// PURPOSE:	 Opens the victory dialog box
//	- Called in Game Moving Functions.cpp
//
void CallVictory();

bool DisableMenuItem(HWND, UINT);

bool EnableMenuItem(HWND, UINT);

static POINT NewPoint(int x, int y)
{
	POINT newPoint;
	newPoint.x = x;
	newPoint.y = y;
	return newPoint;
}

// playMusic - set when background music should be playing
extern bool playMusic;

extern bool playSoundEffects;

// hWnd - a handle to the main window
extern HWND hWnd;

extern const int FRAMES_PER_SECOND;