// Chips Challenge.cpp : Defines the entry point for the application.

#include "stdafx.h"

#define MAX_LOADSTRING 100
const int FRAMES_PER_SECOND = 10;

// Global Variables:
HINSTANCE hInst;								// current instance
HWND hWnd;										// main window
Game game;										// game instance
TCHAR szTitle[MAX_LOADSTRING];					// The title bar text
TCHAR szWindowClass[MAX_LOADSTRING];			// the main window class name

// Drawing objects - declared here to expediate the repetitive drawing process
HDC hdc;
HDC hdcmem;
HDC hbcmem;
HBITMAP colorSprites, blackWhiteSprites, numbers;
HBITMAP hdcbmold, hdcbm;
HBITMAP bg, side, mainCont, tmpbm, tmpold, bm_left, bm_right, sidebg, win_bm;
PAINTSTRUCT ps;
RECT rc;
HDC tmphdc;
HDC tmp;
HFONT font;
HBRUSH hbr;
HPEN pen;
BITMAP structBitmapHeader;
HGDIOBJ hBitmap;
HCURSOR crosshairs = LoadCursorW(hInst, IDC_CROSS);
HCURSOR normal = LoadCursorW(hInst, IDC_ARROW);
POINT cursor;

int animationCounter = 0, animationFrame;
int color;
int modifier;
bool playSoundEffects;
bool playMusic;
bool addIgnorePasswordsOption = false, ignorePassword;
bool addOddEvenControl = false;

// Forward declarations of functions included in this code module:
ATOM				MyRegisterClass(HINSTANCE hInstance);
BOOL				InitInstance(HINSTANCE, int);
BOOL				ToggleMenu(UINT);
LRESULT CALLBACK	WndProc(HWND, UINT, WPARAM, LPARAM);
INT_PTR CALLBACK	About(HWND, UINT, WPARAM, LPARAM);
INT_PTR CALLBACK	BestTimes(HWND, UINT, WPARAM, LPARAM);
INT_PTR CALLBACK	PassEntry(HWND, UINT, WPARAM, LPARAM);
INT_PTR CALLBACK	GoTo(HWND, UINT, WPARAM, LPARAM);
INT_PTR CALLBACK	Victory(HWND, UINT, WPARAM, LPARAM);

int APIENTRY wWinMain(HINSTANCE hInstance, HINSTANCE hPrevInstance, LPTSTR lpCmdLine, int nCmdShow)
{
	UNREFERENCED_PARAMETER(hPrevInstance);
	UNREFERENCED_PARAMETER(lpCmdLine);

	// TODO: Place code here.
	MSG msg;
	HACCEL hAccelTable;

	// Initialize global strings
	LoadString(hInstance, IDS_APP_TITLE, szTitle, MAX_LOADSTRING);
	LoadString(hInstance, IDC_CHIPSCHALLENGE, szWindowClass, MAX_LOADSTRING);
	MyRegisterClass(hInstance);

	// Perform application initialization:
	if (!InitInstance (hInstance, nCmdShow))
		return FALSE;

	hAccelTable = LoadAccelerators(hInstance, MAKEINTRESOURCE(IDC_CHIPSCHALLENGE));

	// Main message loop:
	while (GetMessage(&msg, NULL, 0, 0))
	{
		if (!TranslateAccelerator(msg.hwnd, hAccelTable, &msg))
		{
			TranslateMessage(&msg);
			DispatchMessage(&msg);
		}
	}

	return (int) msg.wParam;
}

//
//  FUNCTION: MyRegisterClass()
//
//  PURPOSE: Registers the window class.
//
//  COMMENTS:
//
//    This function and its usage are only necessary if you want this code
//    to be compatible with Win32 systems prior to the 'RegisterClassEx'
//    function that was added to Windows 95. It is important to call this function
//    so that the application will get 'well formed' small icons associated
//    with it.
//
ATOM MyRegisterClass(HINSTANCE hInstance)
{
	WNDCLASSEX wcex;

	wcex.cbSize = sizeof(WNDCLASSEX);

	wcex.style			= CS_HREDRAW | CS_VREDRAW;
	wcex.lpfnWndProc	= WndProc;
	wcex.cbClsExtra		= 0;
	wcex.cbWndExtra		= 0;
	wcex.hInstance		= hInstance;
	wcex.hIcon			= LoadIcon(hInstance, MAKEINTRESOURCE(IDI_CHIPSCHALLENGE));
	wcex.hCursor		= LoadCursor(NULL, IDC_ARROW);
	wcex.hbrBackground	= (HBRUSH)(COLOR_WINDOW+1);
	wcex.lpszMenuName	= MAKEINTRESOURCE(IDC_CHIPSCHALLENGE);
	wcex.lpszClassName	= szWindowClass;
	wcex.hIconSm		= LoadIcon(wcex.hInstance, MAKEINTRESOURCE(IDI_CHIPSCHALLENGE));

	return RegisterClassEx(&wcex);
}

//
//   FUNCTION: InitInstance(HINSTANCE, int)
//
//   PURPOSE: Saves instance handle and creates main window
//
//   COMMENTS:
//
//        In this function, we save the instance handle in a global variable and
//        create and display the main program window.
//
BOOL InitInstance(HINSTANCE hInstance, int nCmdShow)
{
	game.saveData.replace("Midi", itos((playMusic = (game.saveData["Midi"] == "1"))));

	string cur_level = game.saveData["Current Level"];

	if (cur_level != "")
		game.map.Load(game, atoi(cur_level.c_str()));
	else
		game.map.Load(game, 1);

	_tcscat_s(szTitle, MAX_LOADSTRING, stows(" - ").c_str());
	_tcscat_s(szTitle, MAX_LOADSTRING, stows(game.map.levelTitle).c_str());

	hInst = hInstance;

	hWnd = CreateWindow(szWindowClass, szTitle, (SS_BITMAP|WS_TILED|WS_SYSMENU|WS_MINIMIZEBOX), CW_USEDEFAULT, CW_USEDEFAULT, 518, 401, NULL, NULL, hInstance, NULL);

	if (!hWnd)
		return FALSE;

	game.loadSounds();

	ShowWindow(hWnd, nCmdShow);
	UpdateWindow(hWnd);

	return TRUE;
}

// Globalized function to call victory dialog
void CallVictory() { DialogBox(hInst, MAKEINTRESOURCE(IDD_VICTORY), hWnd, Victory); }

// Error function
void ReportError(const string text) { MessageBox(hWnd, stows(text).c_str(), L"Error", MB_OK | MB_ICONEXCLAMATION); }

//
//  FUNCTION: HelpTopic(const int);
//
//  PURPOSE:  Takes in a help topic number and returns a wide string
//			  consisting of a full path to this help topic
//
wstring HelpTopic(char* topic)
{
	string s = _getcwd(NULL, 0);

	s += "\\CHIPS.CHM";

	if (topic != "")
		s += string("::/html\\topic") + topic + string(".htm");

	return stows(s);
}

UINT ModifyMenuItem(HWND hWnd, UINT item, function<void (UINT&)> modifyState)
{
	MENUITEMINFO menuItem = {sizeof(MENUITEMINFO)};
	menuItem.fMask = MIIM_STATE;

	GetMenuItemInfo(GetMenu(hWnd), item, FALSE, &menuItem);
	modifyState(menuItem.fState);
	SetMenuItemInfo(GetMenu(hWnd), item, FALSE, &menuItem);

	return menuItem.fState;
}

bool ToggleMenuItemCheck(HWND hWnd, UINT item)
{
	return ModifyMenuItem(hWnd, item, [](UINT& state) { state = (state == MFS_CHECKED) ? MFS_UNCHECKED : MFS_CHECKED; }) == MFS_CHECKED;
}

bool DisableMenuItem(HWND hWnd, UINT item)
{
	return ModifyMenuItem(hWnd, item, [](UINT& state) { if (state == MFS_ENABLED) state = MFS_DISABLED; }) == MFS_ENABLED;
}

bool EnableMenuItem(HWND hWnd, UINT item)
{
	return ModifyMenuItem(hWnd, item, [](UINT& state) { if (state == MFS_DISABLED) state = MFS_ENABLED; }) == MFS_ENABLED;
}

void processGameFrame()
{
	if (game.isActive)
	{
		// DO NOT MODIFY THE ORDER OF THE FOLLOWING FIVE LINES
		game.handleMovingBlocks();
		game.handleChip();
		if (game.chip.isDead)
			return;
		game.handleMonsters();

		if (game.chipHasHitMonster()) // If a monster hit chip
		{
			game.death(3);
			return;
		}

		if (game.currentFrame == 0)
		{
			game.currentFrame = FRAMES_PER_SECOND;
			game.timeLeft--;

			if (game.timeLeft < 16 && game.currentFrame % FRAMES_PER_SECOND == 0 && game.map.timeLimit != 0)
				game.soundEffects["TickSound"].play();

			DrawMap();

			if (game.timeLeft == 0)
			{
				game.isActive = false;
				MessageBox(hWnd, L"Oops! Out of time!", L"Chip's Challenge", MB_OK);
				game.map.Load(game, game.map.levelNumber);
			}
		}
		else
			game.currentFrame--;
	}

	if (game.isBeaten)
	{
		if (animationCounter > 800)
		{
			game.isActive = game.isLoaded = false;

			if (animationCounter == 802)
			{
				animationCounter++;

				game.isBeaten = false;
				MessageBox(hWnd, L"Great Job, Chip!\nYou did it! You finished the challenge!", L"Chip's Challenge", MB_OK);
				game.isBeaten = true;
			}

			InvalidateRect(hWnd, NULL, true);
			TCHAR buff[1001];
			LoadString(hInst, MELINDA_MSG, buff, 1000);
			MessageBox(hWnd, buff, L"Chip's Challenge", MB_OK);

			int timeBonus  = game.timeLeft * 10;
			int levelBonus = (int) floor((game.map.levelNumber * 500) * (pow(0.8, game.actualTries - 1)));
			int levelScore, totalScore, levelTime, newLevelScore;

			if (levelBonus < 500)
				levelBonus = 500;

			string levelString = game.saveData["Level" + itos(game.map.levelNumber)];

			int i;

			if (levelString.find(',') != -1 && levelString.find(',', levelString.find(',') + 1) != -1)
				levelTime = stoi(levelString.substr(i = levelString.find(',') + 1, levelString.find(',', i + 1) - i));
			else
				levelTime = 0;

			if (levelString.find(',') != -1 && levelString.find(',', levelString.find(',') + 1) != -1)
				levelScore = stoi(levelString.substr(levelString.find(',', levelString.find(',') + 1) + 1));
			else
				levelScore = 0;

			levelString = game.saveData["Current Score"];

			if (levelString == "")
				totalScore = 0;
			else
				totalScore = stoi(levelString);

			newLevelScore = levelBonus + timeBonus;
			if (newLevelScore > levelScore)
				totalScore += newLevelScore - levelScore;

			// Update INI
			game.saveData.replace("Current Score", itos(totalScore));
			newLevelScore = (newLevelScore > levelScore) ? newLevelScore : levelScore;
			string update = game.map.password + "," + itos(game.timeLeft) + "," + itos(newLevelScore);
			if (game.saveData["Level" + itos(game.map.levelNumber)] == update) 
				game.saveData.replace("Level" + itos(game.map.levelNumber), update);

			int complete = 0, highest = atoi(game.saveData["Highest Level"].c_str());
			for (int i = 1; i <= highest; i++)
			{
				if (game.saveData["Level" + itos(i)].find(",") != -1)
					complete++;
			}

			string str = "You completed ";
			str.append(itos(complete));
			str.append(" levels, and your total score for the challenge is ");
			str.append(itos(totalScore));
			str.append(" points.\n\nYou can still improve your score, by completing levels that you skipped, ");
			str.append("and getting better times on each level. When you replay a level, if your new score is");
			str.append(" better than your old, your score will be adjusted by the difference.  Select Best ");
			str.append("Times from the Game menu to see your scores for each level.");
			MessageBox(hWnd, stows(str).c_str(), L"Chip's Challenge", MB_OK);
		}
		else
		{
			if (animationCounter == 0)
			{
				animationCounter = 32;
				animationFrame = 0;
			}
			else if (animationCounter <= 800)
				animationCounter += 2;

			InvalidateRect(hWnd, NULL, true);

			double counterEvenRoot = sqrt((double)(animationCounter - 32) / 2);
			double counterOddRoot = sqrt((double)((animationCounter - 32) / 2) + 0.25);

			if (counterEvenRoot == floor(counterEvenRoot) || counterOddRoot * 2 == floor(counterOddRoot * 2))
				animationFrame++;

			SetTimer(hWnd, NULL, 250 / FRAMES_PER_SECOND, NULL);
		}
	}
	SetTimer(hWnd, NULL, 1000 / FRAMES_PER_SECOND, NULL);
}

void handleKeyPress(Game& game, int newDirection, bool& keyIsPressed)
{
	if (!game.isPaused)
	{
		if (game.recentKeyPresses.size() < 2 && !keyIsPressed)
		{
			if (game.recentKeyPresses.size() > 0)
			{
				if (game.recentKeyPresses.back() != newDirection)
					game.recentKeyPresses.push(direction(newDirection));
			}
			else
				game.recentKeyPresses.push(direction(newDirection));
		}

		keyIsPressed = true;

		if (!game.isStarted)
		{
			game.isActive = true;
			processGameFrame();
		}
	}
}

void updateTitle(HWND hWnd, Game& game)
{
	string str = "Chip's Challenge - " + game.map.levelTitle;

	if (game.oddEvenLabel)
	{
		str[str.length() - 1] = ' ';
		str += (game.currentFrame % 2) == 0 ? "(odd)" : "(even)";
	}

	TCHAR* tch = new TCHAR[str.length() + 1];
	mbstowcs_s(NULL, tch, str.length() + 1, str.c_str(), str.length());
	SetWindowText(hWnd, tch);
}

//
//  FUNCTION: WndProc(HWND, UINT, WPARAM, LPARAM)
//
//  PURPOSE:  Processes messages for the main window.
//
//  WM_COMMAND	- process the application menu
//  WM_PAINT	- Paint the main window
//  WM_DESTROY	- post a quit message and return
//
//
LRESULT CALLBACK WndProc(HWND hWnd, UINT message, WPARAM wParam, LPARAM lParam)
{
	int wmId, wmEvent;
	
	switch (message)
	{
	case WM_ERASEBKGND:
		return true;
	case WM_TIMER:
		processGameFrame();
		break;
	case WM_SETCURSOR:
		{
			GetCursorPos(&cursor);
			ScreenToClient(hWnd, &cursor); // map to current window

			if (cursor.x >= 32 && cursor.x < 320 && cursor.y >= 32 && cursor.y < 320)
				SetCursor(crosshairs);
			else
				SetCursor(normal);
		}
		break;
	case WM_LBUTTONDOWN:
		{
			if (!game.isPaused)
			{
				GetCursorPos(&cursor);
				ScreenToClient(hWnd, &cursor); // map to current window

				if (cursor.x >= 32 && cursor.x < 320 && cursor.y >= 32 && cursor.y < 320)
				{
					if (!game.isStarted)
					{
						game.isActive = true;
						processGameFrame();
					}

					game.clickedPoint.x = ((int)cursor.x - 32) / 32 + (game.centerTileLocation.x - 4);
					game.clickedPoint.y = ((int)cursor.y - 32) / 32 + (game.centerTileLocation.y - 4);
				}
			}
		}
		break;
	case WM_KILLFOCUS:
		if (!game.chip.isDead)
		{
			game.isPaused = true;
			game.isActive = false;

			DrawMap();
		}
		break;
	case WM_SETFOCUS:
		game.isPaused = false;

		if (game.isStarted && !game.chip.isDead)
			game.isActive = true;

		DrawMap();
		processGameFrame();
		break;
	case WM_KEYDOWN:
		switch (wParam)
		{
		case VK_UP:
			handleKeyPress(game, UP, game.upKeyIsPressed);
			break;
		case VK_DOWN:
			handleKeyPress(game, DOWN, game.downKeyIsPressed);
			break;
		case VK_LEFT:
			handleKeyPress(game, LEFT, game.leftKeyIsPressed);
			break;
		case VK_RIGHT:
			handleKeyPress(game, RIGHT, game.rightKeyIsPressed);
			break;
		}
		break;
	case WM_KEYUP:
		switch (wParam)
		{
		case VK_UP:
			game.upKeyIsPressed = false;
			break;
		case VK_DOWN:
			game.downKeyIsPressed = false;
			break;
		case VK_LEFT:
			game.leftKeyIsPressed = false;
			break;
		case VK_RIGHT:
			game.rightKeyIsPressed = false;
			break;
		}
		break;
	case WM_CREATE:
		{
			game.currentFrame = FRAMES_PER_SECOND;

			hdc = GetDC(hWnd);
			hdcmem = CreateCompatibleDC(hdc);
			GetClientRect(hWnd, &rc);

			hdcbm = CreateCompatibleBitmap(hdc, rc.right, rc.bottom);
			hbcmem = CreateCompatibleDC(hdcmem);
			hdcbmold = (HBITMAP)SelectObject(hdcmem, hdcbm);

			bg					= LoadBitmap(hInst, MAKEINTRESOURCE(IDB_BACKGROUND));
			mainCont			= LoadBitmap(hInst, MAKEINTRESOURCE(IDB_GAME_CONT));
			side				= LoadBitmap(hInst, MAKEINTRESOURCE(IDB_SIDEINFO));
			colorSprites		= LoadBitmap(hInst, MAKEINTRESOURCE(IDB_COLOR_SPRITES));
			blackWhiteSprites	= LoadBitmap(hInst, MAKEINTRESOURCE(IDB_BLACKWHITE_SPRITES));
			sidebg				= LoadBitmap(hInst, MAKEINTRESOURCE(IDB_SIDEBG));
			numbers				= LoadBitmap(hInst, MAKEINTRESOURCE(IDB_NUMBERS));
			bm_left				= LoadBitmap(hInst, MAKEINTRESOURCE(IDB_LEFT));
			bm_right			= LoadBitmap(hInst, MAKEINTRESOURCE(IDB_RIGHT));
			win_bm				= LoadBitmap(hInst, MAKEINTRESOURCE(IDB_VICTORY));
			if(bg == NULL || mainCont == NULL || side == NULL || colorSprites == NULL || blackWhiteSprites == NULL || sidebg == NULL || numbers == NULL || bm_left == NULL || bm_right == NULL)
				ReportError("A bitmap failed to load.");

			if (playSoundEffects = (game.saveData["Sounds"] == "1"))
				ToggleMenuItemCheck(hWnd, ID_OPTIONS_SOUNDEFFECTS);

			if ((game.saveData["Midi"] == "1"))
				ToggleMenuItemCheck(hWnd, ID_OPTIONS_BACKGROUNDMUSIC);

			if (color = (game.saveData["Color"] == "1" || game.saveData["Color"] == ""))
				ToggleMenuItemCheck(hWnd, ID_OPTIONS_COLOR);

			SetTimer(hWnd, NULL, 0, NULL);
		}
		break;
	case WM_COMMAND:
		wmId = LOWORD(wParam);
		wmEvent = HIWORD(wParam);

		// Parse the menu selections:
		switch (wmId)
		{
		case ID_ADD_IGNORE:
			if (!addIgnorePasswordsOption)
			{
				HMENU menu = GetSubMenu(GetMenu(hWnd), 1);
				ignorePassword = addIgnorePasswordsOption = true;
				AppendMenu(menu, MF_STRING | MF_CHECKED, ID_GAME_IGNOREPASSWORDS, L"&Ignore Passwords");
			}
			break;
		case ID_GAME_IGNOREPASSWORDS:
			ignorePassword = ToggleMenuItemCheck(hWnd, ID_GAME_IGNOREPASSWORDS);
			break;
		case ID_ADD_ODDEVEN_CONTROL:
			if (!addOddEvenControl)
			{
				HMENU menu = GetSubMenu(GetMenu(hWnd), 1);
				addOddEvenControl = game.oddEvenLabel = true;
				AppendMenu(menu, MF_STRING | MF_CHECKED, ID_GAME_ODDEVEN_LABEL, L"&Odd/even label");

				if (!game.isStarted)
					updateTitle(hWnd, game);
				break;
			}
		case ID_GAME_ODDEVEN_LABEL:
			game.oddEvenLabel = ToggleMenuItemCheck(hWnd, ID_GAME_ODDEVEN_LABEL);

			updateTitle(hWnd, game);
		case ID_GAME_NEWGAME:
			{
				if (MessageBox(hWnd, L"Starting a new game will begin you back at level 1, reset your score to zero, and forget the passwords to any levels you have visited.  Is this what you want?", L"Chip's Challenge", MB_YESNO) == IDYES)
				{
					char* environmentString = new char[MAX_PATH];
					size_t len = MAX_PATH;
					_dupenv_s(&environmentString, &len, "APPDATA");
					DeleteFile(stows(string(string(environmentString) + "\\Chip's Challenge\\entpack.ini")).c_str());
					delete [] environmentString;

					game.saveData = INI();
					game.map.Load(game);
				}
			}
			break;
		case ID_GAME_PAUSE:
			if (!game.isStarted)
				game.isStarted = true;

			if ((game.isActive = !(game.isPaused = ToggleMenuItemCheck(hWnd, ID_GAME_PAUSE))) && game.isStarted)
				processGameFrame();

			DrawMap();
			break;
		case ID_GAME_BESTTIMES:
			DialogBox(hInst, MAKEINTRESOURCE(IDD_BESTTIMES), hWnd, BestTimes);
			break;
		case ID_OPTIONS_BACKGROUNDMUSIC:
		{
			if (playMusic = ToggleMenuItemCheck(hWnd, ID_OPTIONS_BACKGROUNDMUSIC))
				game.backgroundMusic.start(game.map.levelNumber);
			else
				game.backgroundMusic.stop();

			game.saveData.replace("Midi", itos(playMusic));
		}
			break;
		case ID_OPTIONS_SOUNDEFFECTS:
		{
			game.saveData.replace("Sounds", itos(playSoundEffects = ToggleMenuItemCheck(hWnd, ID_OPTIONS_SOUNDEFFECTS)));
		}
			break;
		case ID_OPTIONS_COLOR:
		{
			game.saveData.replace("Color",itos(color = ToggleMenuItemCheck(hWnd, ID_OPTIONS_COLOR)));
			DrawMap();
		}
			break;
		case ID_HELP_ABOUTCHIPSCHALLENGE:
			DialogBox(hInst, MAKEINTRESOURCE(IDD_ABOUTBOX), hWnd, About);
			break;
		case ID_HELP_CONTENTS:
			if (!HtmlHelp(hWnd, HelpTopic("").c_str(), HH_DISPLAY_TOC, NULL))
				ReportError("CHIPS.CHM may be missing");
			break;
		case ID_HELP_HOWTOPLAY:
			if (!HtmlHelp(hWnd, HelpTopic("22").c_str(), HH_DISPLAY_TOPIC, NULL))
				ReportError("CHIPS.CHM may be missing");
			break;
		case ID_HELP_COMMANDS:
			if (!HtmlHelp(hWnd, HelpTopic("23").c_str(), HH_DISPLAY_TOPIC, NULL))
				ReportError("CHIPS.CHM may be missing");
			break;
		case ID_LEVEL_RESTART:
			game.incrementTriesAndReloadMap();
			break;
		case ID_LEVEL_NEXT:
			if (game.saveData["Level" + itos(game.map.levelNumber + 1)] == "" && !ignorePassword)
				DialogBoxParam(hInst, MAKEINTRESOURCE(IDD_PASSENTRY), hWnd, PassEntry, 1);
			else if (game.map.levelNumber + 1 <= game.totalLevels)
				game.map.Load(game, game.map.levelNumber + 1);
			break;
		case ID_LEVEL_PREVIOUS:
			if (game.saveData["Level" + itos(game.map.levelNumber - 1)] == "" && !ignorePassword)
				DialogBoxParam(hInst, MAKEINTRESOURCE(IDD_PASSENTRY), hWnd, PassEntry, -1);
			else
				game.map.Load(game, game.map.levelNumber - 1);
			break;
		case ID_LEVEL_GOTO:
			DrawMap();
			DialogBox(hInst, MAKEINTRESOURCE(IDD_GOTO), hWnd, GoTo);
			break;
		case ID_GAME_EXIT:
			DestroyWindow(hWnd);
			break;
		default:
			return DefWindowProc(hWnd, message, wParam, lParam);
		}
		break;
	case WM_PAINT:
	{
		BeginPaint(hWnd, &ps);

		// Background
		HBITMAP hbcold = (HBITMAP)SelectObject(hbcmem, bg);

		BitBlt(hdcmem, 0, 0, 237, 196, hbcmem, 0, 0, SRCCOPY);
		BitBlt(hdcmem, 237, 0, 237, 196, hbcmem, 0, 0, SRCCOPY);
		BitBlt(hdcmem, 237 * 2, 0, 237, 196, hbcmem, 0, 0, SRCCOPY);
		BitBlt(hdcmem, 0, 196, 237, 196, hbcmem, 0, 0, SRCCOPY);
		BitBlt(hdcmem, 237, 196, 237, 196, hbcmem, 0, 0, SRCCOPY);
		BitBlt(hdcmem, 237 * 2, 196, 237, 196, hbcmem, 0, 0, SRCCOPY);

		// Main Game Container
		SelectObject(hbcmem, mainCont);

		BitBlt(hdcmem, 26, 26, 300, 300, hbcmem, 0, 0, SRCCOPY);

		// Side Info
		SelectObject(hbcmem, side);

		BitBlt(hdcmem, 339, 26, 154, 300, hbcmem, 0, 0, SRCCOPY);
		DrawLevelNumber(game.map.levelNumber);

		if (color)
			SelectObject(hbcmem, colorSprites);
		else
			SelectObject(hbcmem, blackWhiteSprites);

		// Find x and y coordinate for the top left of the visible screen
		game.centerTileLocation.x = game.chip.x;
		game.centerTileLocation.y = game.chip.y;

		int currentX = 0, currentY = 0;
		if (game.centerTileLocation.x < 4)  game.centerTileLocation.x = 4;
		if (game.centerTileLocation.x > 27) game.centerTileLocation.x = 27;
		if (game.centerTileLocation.y < 4)  game.centerTileLocation.y = 4;
		if (game.centerTileLocation.y > 27) game.centerTileLocation.y = 27;

		int x = game.centerTileLocation.x;
		int y = game.centerTileLocation.y;
		x -= 4;
		y -= 4;

		for (int h = game.map.layers.size() - 1; h >= 0; h--)
		{
			// Draw tiles
			for (int i = 0; i < 9; i++)
			{
				for (int j = 0; j < 9; j++)
				{
					if (((((unsigned int) (h + 1)) < game.map.layers.size() && game.map.layers[h + 1][x + i][y + j] != 0) && game.map.layers[h][x + i][y + j] >= 64 && game.map.layers[h][x + i][y + j] <= 111))
					{
						game.getPosition(game.map.layers[h][x + i][y + j].get(), currentX, currentY, true);
						BitBlt(hdcmem, (i * 32) + 32, (j * 32) + 32, 32, 32, hbcmem, currentX + 96, currentY, SRCPAINT);
						BitBlt(hdcmem, (i * 32) + 32, (j * 32) + 32, 32, 32, hbcmem, currentX, currentY, SRCAND);
					}
					else
					{
						game.getPosition(game.map.layers[h][x + i][y + j].get(), currentX, currentY, false);
						BitBlt(hdcmem, (i * 32) + 32, (j * 32) + 32, 32, 32, hbcmem, currentX, currentY, SRCCOPY);
					}
				}
			}
		}

		// Victory animation
		if (game.isBeaten)
		{
			if (game.isLoaded)
			{
				bool armsLifted = false;

				int ci_x = (animationCounter > 287) ? ((((int)(animationCounter / 32)) % 2 == 1) ? armsLifted = true, 96 : 288) : 288;
				int ci_y = (animationCounter > 287) ? ((((int)(animationCounter / 32)) % 2 == 1) ? armsLifted = true, 288 : 448) : 448;
				int i_x = (game.chip.x > 4 && game.chip.x < 29) ? 4 : ((game.chip.x <= 4) ? game.chip.x : game.chip.x - 24);
				int i_y = (game.chip.y > 4 && game.chip.y < 29) ? 4 : ((game.chip.y <= 4) ? game.chip.y : game.chip.y - 24);

				i_x = i_x * 32 - ((animationCounter / 2) - 16) + 32; // x for drawn image
				i_x = (animationCounter > 287) ? 32 : ((i_x < 32) ? 32 : (((i_x + animationCounter) > 320) ? (320 - animationCounter) : i_x));
				i_y = i_y * 32 - ((animationCounter / 2) - 16) + 32; // y for drawn image
				i_y = (animationCounter > 287) ? 32 : ((i_y < 32) ? 32 : (((i_y + animationCounter) > 320) ? (320 - animationCounter) : i_y));

				int s_x = (animationFrame % 3 == 0 && animationCounter < 288) ? 32 : 96;
				int s_y = (animationCounter > 287) ? 320 : ((animationFrame % 3 == 0) ? 160 : 320 + ((animationFrame % 3) - 1) * 32);

				StretchBlt(hdcmem, i_x, i_y, ((animationCounter > 288) ? 288 : animationCounter), ((animationCounter > 288) ? 288 : animationCounter), hbcmem, s_x, s_y, 32, 32, SRCCOPY);
				StretchBlt(hdcmem, i_x, i_y, ((animationCounter > 288) ? 288 : animationCounter), ((animationCounter > 288) ? 288 : animationCounter), hbcmem, ci_x + ((armsLifted) ? 0 : 96), ci_y, 32, 32, SRCPAINT);
				StretchBlt(hdcmem, i_x, i_y, ((animationCounter > 288) ? 288 : animationCounter), ((animationCounter > 288) ? 288 : animationCounter), hbcmem, ci_x, ci_y, 32, 32, SRCAND);
			}
			else
			{
				SelectObject(hbcmem, win_bm);
				BitBlt(hdcmem, 32, 32, 288, 288, hbcmem, 0, 0, SRCCOPY);
				game.isBeaten = false;
			}
		}

		// Show level title
		if (!game.isStarted)
		{
			tmphdc = CreateCompatibleDC(hdcmem);
			tmp = CreateCompatibleDC(tmphdc);
			GetClientRect(hWnd, &rc);
			string str = game.map.levelTitle.substr(0, game.map.levelTitle.length() - 1) + "\nPassword: " + game.map.password;
			TCHAR* tch = new TCHAR[str.length() + 1];
			mbstowcs_s(NULL, tch, str.length() + 1, str.c_str(), str.length());
			font = CreateFont(25, 0, 0, 0, FW_BOLD, false, false, false, ANSI_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, DEFAULT_QUALITY, FF_DONTCARE, NULL);
			tmpold = (HBITMAP)SelectObject(tmp, font);
			DrawText(tmp, tch, str.length(), &rc, DT_CALCRECT);
			rc.right += 16;
			tmpbm = CreateCompatibleBitmap(hdcmem, rc.right, rc.bottom);
			HBITMAP tmphdcold = (HBITMAP)SelectObject(tmphdc, tmpbm);

			hbr = CreateSolidBrush(RGB(255, 255, 255));
			pen = CreatePen(PS_SOLID, 1, RGB(255, 255, 255));
			HBITMAP hdcold = (HBITMAP)SelectObject(hdcmem, pen);
			SelectObject(hdcmem, hbr);
			Rectangle(hdcmem, 176 - (rc.right / 2), 243, 177 + (rc.right / 2), 248);
			hbr = CreateSolidBrush(RGB(128, 128, 128));
			pen = CreatePen(PS_SOLID, 1, RGB(128, 128, 128));
			SelectObject(hdcmem, pen);
			SelectObject(hdcmem, hbr);
			Rectangle(hdcmem, 176 - (rc.right / 2), 294, 177 + (rc.right / 2), 299);

			SelectObject(hbcmem, bm_left);
			BitBlt(hdcmem, 176 - (rc.right / 2) - 4, 243, 4, 56, hbcmem, 0, 0, SRCCOPY);
			SelectObject(hbcmem, bm_right);
			BitBlt(hdcmem, 176 + (rc.right / 2) + ((rc.right % 2) != 0), 243, 4, 56, hbcmem, 0, 0, SRCCOPY);

			SelectObject(tmphdc, font);
			SetTextColor(tmphdc, RGB(255, 255, 0));
			SetBkColor(tmphdc, RGB(0, 0, 0));
			DrawText(tmphdc, tch, str.length(), &rc, DT_CENTER);
			memset( &structBitmapHeader, 0, sizeof(BITMAP) );
			hBitmap = GetCurrentObject(tmphdc, OBJ_BITMAP);
			GetObject(hBitmap, sizeof(BITMAP), &structBitmapHeader);
			BitBlt(hdcmem, 176 - (rc.right / 2), 247, structBitmapHeader.bmWidth, structBitmapHeader.bmHeight, tmphdc, 0, 0, SRCCOPY);

			SelectObject(hdcmem, hdcold);
			SelectObject(tmphdc, tmphdcold);
			DeleteDC(tmphdc);
			DeleteObject(tmphdcold);
			SelectObject(tmp, tmpold);
			DeleteDC(tmp);
			DeleteObject(tmpold);
			DeleteObject(hbr);
			DeleteObject(pen);
			DeleteDC(tmphdc);
			DeleteObject(tmpbm);
			DeleteObject(font);
			delete [] tch;
			tch = NULL;
		}

		if (game.isPaused)
		{
			rc.top = 32;
			rc.left = 32;
			rc.bottom = 330;
			rc.right = 330;
			BitBlt(hdcmem, 32, 32, 288, 288, NULL, 0, 0, BLACKNESS);
			font = CreateFont(50, 0, 0, 0, FW_NORMAL, false, false, false, ANSI_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, DEFAULT_QUALITY, FF_DONTCARE, NULL);
			HBITMAP hdcold = (HBITMAP)SelectObject(hdcmem, font);
			SetTextColor(hdcmem, RGB(255, 0, 0));
			SetBkColor(hdcmem, RGB(0, 0, 0));
			DrawText(hdcmem, L"PAUSED", 6, &rc, (DT_CENTER + DT_SINGLELINE + DT_VCENTER));

			SelectObject(hdcmem, hdcold);
			DeleteObject(font);
		}

		SelectObject(hbcmem, numbers);

		for (int i = 100; i > 0; i /= 10) // Chips and time left
		{
			int tmp;
			if (i == 100)
				tmp = game.chipsLeft / 100;

			if (i == 10)
				tmp = ((game.chipsLeft % 100) - (game.chipsLeft % 10)) / 10;

			if (i == 1)
				tmp = game.chipsLeft % 10;

			if (game.chipsLeft < i && i > 1)
				tmp = 10;

			int currentY = game.getNumberPosition(tmp, (game.chipsLeft == 0));
			BitBlt(hdcmem, 417 + ((3 - (int)floor(log10((double)i)) * 17)), 215, 17, 23, hbcmem, 0, currentY, SRCCOPY);

			if (i == 100)
				tmp = game.timeLeft / 100;

			if (i == 10)
				tmp = ((game.timeLeft % 100) - (game.timeLeft % 10)) / 10;

			if (i == 1)
				tmp = game.timeLeft % 10;

			if (game.timeLeft < i && i > 1)
				tmp = 10;

			if (game.map.timeLimit == 0)
				tmp = 11;

			currentY = game.getNumberPosition(tmp, (game.timeLeft < 16 || game.map.timeLimit == 0));
			BitBlt(hdcmem, 369 + ((3 - (int)floor(log10((double)i))) * 17), 125, 17, 23, hbcmem, 0, currentY, SRCCOPY);
		}

		if (game.chip.isOnHintTile)
		{
			SelectObject(hbcmem, sidebg);
			BitBlt(hdcmem, 353, 165, 127, 146, hbcmem, 0, 0, SRCCOPY);
			font = CreateFont(18, 0, 0, 0, FW_BOLD, true, false, false, ANSI_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, DEFAULT_QUALITY, FF_DONTCARE, NULL);
			SelectObject(hdcmem, font);
			SetTextColor(hdcmem, RGB(0, 255, 255));
			SetBkColor(hdcmem, RGB(0, 0, 0));
			rc.top = 168;
			rc.left = 356;
			rc.bottom = 308;
			rc.right = 477;
			string str = "Hint: " + game.map.hint;
			TCHAR* tch = new TCHAR[str.length() + 1];
			mbstowcs_s(NULL, tch, str.length(), str.c_str(), str.length());
			DrawText(hdcmem, tch, str.length(), &rc, DT_CENTER + DT_WORDBREAK);
			delete [] tch;
			tch = NULL;

			DeleteObject(font);
		}
		else // LOWER SIDE INFO
		{
			if (color)
				SelectObject(hbcmem, colorSprites);
			else
				SelectObject(hbcmem, blackWhiteSprites);

			if (game.chip.redKeys > 0)
				BitBlt(hdcmem, 352, 247, 32, 32, hbcmem, 192, 160, SRCCOPY);
			else
				BitBlt(hdcmem, 352, 247, 32, 32, hbcmem, 0, 0, SRCCOPY);

			if (game.chip.blueKeys > 0)
				BitBlt(hdcmem, 384, 247, 32, 32, hbcmem, 192, 128, SRCCOPY);
			else
				BitBlt(hdcmem, 384, 247, 32, 32, hbcmem, 0, 0, SRCCOPY);

			if (game.chip.yellowKeys > 0)
				BitBlt(hdcmem, 416, 247, 32, 32, hbcmem, 192, 224, SRCCOPY);
			else
				BitBlt(hdcmem, 416, 247, 32, 32, hbcmem, 0, 0, SRCCOPY);

			if (game.chip.hasGreenKey)
				BitBlt(hdcmem, 448, 247, 32, 32, hbcmem, 192, 192, SRCCOPY);
			else
				BitBlt(hdcmem, 448, 247, 32, 32, hbcmem, 0, 0, SRCCOPY);

			if (game.chip.hasFlippers)
				BitBlt(hdcmem, 352, 279, 32, 32, hbcmem, 192, 256, SRCCOPY);
			else
				BitBlt(hdcmem, 352, 279, 32, 32, hbcmem, 0, 0, SRCCOPY);

			if (game.chip.hasFireBoots)
				BitBlt(hdcmem, 384, 279, 32, 32, hbcmem, 192, 288, SRCCOPY);
			else
				BitBlt(hdcmem, 384, 279, 32, 32, hbcmem, 0, 0, SRCCOPY);

			if (game.chip.hasSkates)
				BitBlt(hdcmem, 416, 279, 32, 32, hbcmem, 192, 320, SRCCOPY);
			else
				BitBlt(hdcmem, 416, 279, 32, 32, hbcmem, 0, 0, SRCCOPY);

			if (game.chip.hasSuctionBoots)
				BitBlt(hdcmem, 448, 279, 32, 32, hbcmem, 192, 352, SRCCOPY);
			else
				BitBlt(hdcmem, 448, 279, 32, 32, hbcmem, 0, 0, SRCCOPY);
		}
		BitBlt(hdc, 0, 0, 518, 401, hdcmem, 0, 0, SRCCOPY);

		SelectObject(hbcmem, hbcold);

		EndPaint(hWnd, &ps);
	}
		break;
	case WM_DESTROY:
		PostQuitMessage(0);
		break;
	default:
		return DefWindowProc(hWnd, message, wParam, lParam);
	}
	return 0;
}

// Message handler for about box.
INT_PTR CALLBACK About(HWND hDlg, UINT message, WPARAM wParam, LPARAM lParam)
{
	UNREFERENCED_PARAMETER(lParam);
	switch (message)
	{
	case WM_INITDIALOG:
		return (INT_PTR)TRUE;

	case WM_COMMAND:
		if (LOWORD(wParam) == IDOK || LOWORD(wParam) == IDCANCEL)
		{
			EndDialog(hDlg, LOWORD(wParam));
			return (INT_PTR)TRUE;
		}
		break;
	}
	return (INT_PTR)FALSE;
}

// Message handler for GoTo box.
INT_PTR CALLBACK GoTo(HWND hDlg, UINT message, WPARAM wParam, LPARAM lParam)
{
	UNREFERENCED_PARAMETER(lParam);
	switch (message)
	{
	case WM_INITDIALOG:
		{
			HWND hwndList = GetDlgItem(hDlg, TXT_GOTO_LEVEL);
			SendMessage(hwndList, EM_LIMITTEXT, 3, 0);
			hwndList = GetDlgItem(hDlg, TXT_GOTO_PASS);
			SendMessage(hwndList, EM_LIMITTEXT, 4, 0);
			return (INT_PTR)TRUE;
		}

	case WM_COMMAND:
		if (LOWORD(wParam) == IDOK)
		{
			TCHAR* tmp = new TCHAR[4];
			tmp[3] = 0;
			char* temp = new char[4];
			temp[3] = 0;
			GetDlgItemText(hDlg, TXT_GOTO_LEVEL, tmp, 4);
			wcstombs_s(NULL, temp, 4, tmp, 4);
			string str = temp;
			if (str == "")
				str = "0";
			int level = atoi(temp);

			delete [] tmp;
			tmp = new TCHAR[5];
			tmp[4] = 0;
			delete [] temp;
			temp = new char[5];
			temp[4] = 0;
			GetDlgItemText(hDlg, TXT_GOTO_PASS, tmp, 5);
			wcstombs_s(NULL, temp, 5, tmp, 5);
			string pass = temp;

			delete [] tmp;
			delete [] temp;

			int lvl;
			if (level != 0)
			{
				if (game.map.TryLoad(game, pass, level))
				{
					EndDialog(hDlg, LOWORD(wParam));
					game.map.Load(game, level);
					EndDialog(hDlg, LOWORD(wParam));
					return (INT_PTR)TRUE;
				}
				else if (game.saveData["Level" + itos(level)] != "" || ignorePassword)
				{
					game.map.Load(game, level);
					EndDialog(hDlg, LOWORD(wParam));
					return (INT_PTR)TRUE;
				}
				else
				{
					EndDialog(hDlg, LOWORD(wParam));
					ReportError("You must enter a valid password.");
					return (INT_PTR)TRUE;
				}
			}
			else
			{
				if (lvl = game.map.TryLoad(game, pass))
				{
					EndDialog(hDlg, LOWORD(wParam));
					game.map.Load(game, lvl);
					EndDialog(hDlg, LOWORD(wParam));
					return (INT_PTR)TRUE;
				}
				else
				{
					EndDialog(hDlg, LOWORD(wParam));
					ReportError("You must enter a valid password.");
					return (INT_PTR)TRUE;
				}
			}
		}
		if (LOWORD(wParam) == IDCANCEL)
		{
			processGameFrame();
			EndDialog(hDlg, LOWORD(wParam));
			return (INT_PTR)TRUE;
		}
		break;
	}
	return (INT_PTR)FALSE;
}

// Message handler for password entry dialog
INT_PTR CALLBACK PassEntry(HWND hDlg, UINT message, WPARAM wParam, LPARAM lParam)
{
	switch (message)
	{
	case WM_INITDIALOG:
		{ 
			HWND hwndList = GetDlgItem(hDlg, ID_PE_TEXT_ENTER);
			SendMessage(hwndList, EM_LIMITTEXT, 4, 0);
			hwndList = GetDlgItem(hDlg, TXT_PASS_ENTRY);
			WCHAR szW[1024];
			modifier = (int)lParam;
			string part = "Please enter the password for level " + itos(game.map.levelNumber + modifier) + ":";
			MultiByteToWideChar(CP_UTF8, 0, part.c_str(), -1, szW, 1024);
			SendMessage(hwndList,WM_SETTEXT,0,(LPARAM)szW);
		}
		return (INT_PTR)TRUE;

	case WM_COMMAND:
		if (LOWORD(wParam) == IDOK)
		{
			TCHAR tmp[5];
			tmp[4] = 0;
			char entered[5];
			entered[4] = 0;
			GetDlgItemText(hDlg, ID_PE_TEXT_ENTER, tmp, 5);
			wcstombs_s(NULL, entered, 5, tmp, 5);
			string str = entered;
			if (game.map.TryLoad(game, str, game.map.levelNumber + modifier))
			{
				EndDialog(hDlg, LOWORD(wParam));
				game.map.Load(game, game.map.levelNumber + modifier);
				return (INT_PTR)TRUE;
			}
			else
			{
				EndDialog(hDlg, LOWORD(wParam));
				ReportError("Sorry, \"" + str + "\" is not the correct password.");
				return (INT_PTR)TRUE;
			}
		}
		if (LOWORD(wParam) == IDCANCEL)
		{
			EndDialog(hDlg, LOWORD(wParam));
			return (INT_PTR)TRUE;
		}
		break;
	}
	return (INT_PTR)FALSE;
}

// Message handler for best times box.
INT_PTR CALLBACK BestTimes(HWND hDlg, UINT message, WPARAM wParam, LPARAM lParam)
{
	UNREFERENCED_PARAMETER(lParam);
	switch (message)
	{
	case WM_INITDIALOG:
		{
			HWND hwndList = GetDlgItem(hDlg, IDC_SCORES_LIST);
			int a = 0, score = 0, totalScore = 0, complete = 0, highest = atoi(game.saveData["Highest Level"].c_str());
			for (int i = 1; i <= highest; i++)
			{
				string seconds, line = game.saveData["Level" + itos(i)];
				if (line.find(",") == -1)
					line = "Level " + itos(i) + ": not completed";
				else
				{
					complete++;
					seconds = line.substr((a = line.find(",")) + 1, line.find(",", a + 1) - a - 1);
					totalScore += score = atoi(line.substr(line.find(",", a + 1) + 1).c_str());
					line = "Level " + itos(i) + ": " + seconds + " seconds, " + itos(score) + " points";
				}
				WCHAR szW[1024];
				MultiByteToWideChar(CP_UTF8, 0, line.c_str(), -1, szW, 1024);
				SendMessage(hwndList,LB_ADDSTRING,0,(LPARAM)szW);
			}

			hwndList = GetDlgItem(hDlg, TXT_LEVELS_COMPLETE);
			WCHAR szW[1024];
			string part = (complete == 1) ? "You have completed 1 level" : "You have completed " + itos(complete) + " levels.";
			MultiByteToWideChar(CP_UTF8, 0, part.c_str(), -1, szW, 1024);
			SendMessage(hwndList,WM_SETTEXT,0,(LPARAM)szW);

			hwndList = GetDlgItem(hDlg, TXT_TOTAL_SCORE);
			part = (totalScore == 1) ? "Your total score is 1 point." : "Your total score is " + itos(totalScore) + " points.";
			MultiByteToWideChar(CP_UTF8, 0, part.c_str(), -1, szW, 1024);
			SendMessage(hwndList,WM_SETTEXT,0,(LPARAM)szW);

		}
		return (INT_PTR)TRUE;
	case WM_COMMAND:
		if (LOWORD(wParam) == IDOK || LOWORD(wParam) == IDCANCEL)
		{
			EndDialog(hDlg, LOWORD(wParam));
			return (INT_PTR)TRUE;
		}
		if (LOWORD(wParam) == IDGOTO)
		{
			HWND listBox = GetDlgItem(hDlg, IDC_SCORES_LIST);
			LRESULT r = SendMessage (listBox , LB_GETCURSEL, 0, 0);
			LRESULT l = SendMessage (listBox , LB_GETTEXTLEN, (WPARAM)r, 0);
			TCHAR* text = new TCHAR [l +1];
			SendMessage (listBox, LB_GETTEXT, (WPARAM)r, (LPARAM)text);
			wstring w = text;
			string s(w.begin(), w.end());
			delete [] text;
			int level = stoi(s.substr(s.find(" ") + 1, s.find(":") - (s.find(" ") + 1)));
			game.map.Load(game, level);
			EndDialog(hDlg, LOWORD(wParam));
			return (INT_PTR)TRUE;
		}
		break;
	}
	switch (HIWORD(wParam))
	{
	case LBN_DBLCLK:
		{
			HWND listBox = GetDlgItem(hDlg, IDC_SCORES_LIST);
			LRESULT r = SendMessage (listBox , LB_GETCURSEL, 0, 0);
			LRESULT l = SendMessage (listBox , LB_GETTEXTLEN, (WPARAM)r, 0);
			TCHAR* text = new TCHAR [l +1];
			SendMessage (listBox, LB_GETTEXT, (WPARAM)r, (LPARAM)text);
			wstring w = text;
			string s(w.begin(), w.end());
			delete [] text;
			int level = stoi(s.substr(s.find(" ") + 1, s.find(":") - (s.find(" ") + 1)));
			game.map.Load(game, level);
			EndDialog(hDlg, LOWORD(wParam));
			return (INT_PTR)TRUE;
		}
	case LBN_SELCHANGE:
		EnableWindow(GetDlgItem(hDlg, IDGOTO ), TRUE);
		break;
	}
	return (INT_PTR)FALSE;
}

INT_PTR CALLBACK Victory(HWND hDlg, UINT message, WPARAM wParam, LPARAM lParam)
{
	UNREFERENCED_PARAMETER(lParam);
	switch (message)
	{
	case WM_INITDIALOG:
		{
			int timeBonus  = game.timeLeft * 10;
			int levelBonus = (int)floor((game.map.levelNumber * 500) * (pow(0.8, game.actualTries - 1)));
			int levelScore, levelTime, totalScore, newLevelScore;

			if (levelBonus < 500)
				levelBonus = 500;

			string temp = game.saveData["Level"+itos(game.map.levelNumber)];
			int i;

			if (temp.find(',') != -1 && temp.find(',', temp.find(',') + 1) != -1)
				levelTime = stoi(temp.substr(i = temp.find(',') + 1, temp.find(',', i + 1) - i));
			else
				levelTime = 0;

			if (temp.find(',') != -1 && temp.find(',', temp.find(',') + 1) != -1)
				levelScore = stoi(temp.substr(temp.find(',', temp.find(',') + 1) + 1));
			else
				levelScore = 0;

			temp = game.saveData["Current Score"];

			if (temp == "")
				totalScore = 0;
			else
				totalScore = stoi(temp);

			newLevelScore = levelBonus + timeBonus;
			if (newLevelScore > levelScore)
				totalScore += newLevelScore - levelScore;

			HWND msg_txt		= GetDlgItem(hDlg, IDC_MSG);
			HWND timebonus_txt	= GetDlgItem(hDlg, IDC_TIMEBONUS);
			HWND lvlbonus_txt	= GetDlgItem(hDlg, IDC_LVLBONUS);
			HWND lvlscore_txt	= GetDlgItem(hDlg, IDC_LVLSCORE);
			HWND totalscore_txt	= GetDlgItem(hDlg, IDC_TOTALSCORE);
			HWND msg2_txt		= GetDlgItem(hDlg, IDC_MSG2);

			WCHAR tmp[1024];

			// Top message
			string msg_str;
			if (game.actualTries == 1)
				msg_str = "Yowser! First Try!";
			else if (game.actualTries < 4)
				msg_str = "Go Bit Buster!";
			else if (game.actualTries < 6)
				msg_str = "Finished! Good Work!";
			else
				msg_str = "At last! You did it!";

			MultiByteToWideChar(CP_UTF8, 0, msg_str.c_str(), -1, tmp, 1024);
			SendMessage(msg_txt,WM_SETTEXT,0,(LPARAM)tmp);

			// Time bonus
			string timebonus_str = "Time Bonus: " + itos(timeBonus);
			MultiByteToWideChar(CP_UTF8, 0, timebonus_str.c_str(), -1, tmp, 1024);
			SendMessage(timebonus_txt,WM_SETTEXT,0,(LPARAM)tmp);

			// Level bonus
			string lvlbonus_str = "Level Bonus: " + itos(levelBonus);
			MultiByteToWideChar(CP_UTF8, 0, lvlbonus_str.c_str(), -1, tmp, 1024);
			SendMessage(lvlbonus_txt,WM_SETTEXT,0,(LPARAM)tmp);

			// Level score
			string lvlscore_str = "Level Score: " + itos(levelBonus + timeBonus);
			MultiByteToWideChar(CP_UTF8, 0, lvlscore_str.c_str(), -1, tmp, 1024);
			SendMessage(lvlscore_txt,WM_SETTEXT,0,(LPARAM)tmp);

			// Level score
			string totalscore_str = "Total Score: " + itos(totalScore);
			MultiByteToWideChar(CP_UTF8, 0, totalscore_str.c_str(), -1, tmp, 1024);
			SendMessage(totalscore_txt,WM_SETTEXT,0,(LPARAM)tmp);

			// Bottom message
			string msg2_str;
			if (levelTime == 0)
				msg2_str = "You have established a time record for this level!";
			else if (game.timeLeft > levelTime) 
				msg2_str = "You beat the previous time record by " + itos(game.timeLeft - levelTime) + " seconds!";
			else if (newLevelScore > levelScore)
				msg2_str = "You increased your score on this level by " + itos(newLevelScore - levelScore) + " points!";
			else
				msg2_str = "";

			MultiByteToWideChar(CP_UTF8, 0, msg2_str.c_str(), -1, tmp, 1024);
			SendMessage(msg2_txt,WM_SETTEXT,0,(LPARAM)tmp);

			// Update INI
			game.saveData.replace("Current Score", itos(totalScore));
			newLevelScore = (newLevelScore > levelScore) ? newLevelScore : levelScore;
			string update = game.map.password + "," + itos(game.timeLeft) + "," + itos(newLevelScore);
			if (msg2_str != "") 
				game.saveData.replace("Level" + itos(game.map.levelNumber), update);
		}
		return (INT_PTR)TRUE;

	case WM_COMMAND:
		if (LOWORD(wParam) == IDOK || LOWORD(wParam) == IDCANCEL)
		{
			EndDialog(hDlg, LOWORD(wParam));
			return (INT_PTR)TRUE;
		}
		break;
	}
	return (INT_PTR)FALSE;
}

// Level number drawing function
void DrawLevelNumber(int num)
{
	HBITMAP bitold = (HBITMAP)SelectObject(hbcmem, numbers);
	// Draw side info
	for (int i = 100; i > 0; i /= 10)
	{
		int tmp;
		if (i == 100)
			tmp = game.map.levelNumber / 100;

		if (i == 10)
			tmp = ((game.map.levelNumber % 100) - (game.map.levelNumber % 10)) / 10;

		if (i == 1)
			tmp = game.map.levelNumber % 10;

		if (game.map.levelNumber < i)
			tmp = 10;

		int ypos = game.getNumberPosition(tmp);
		BitBlt(hdcmem, 369 + ((3 - (int)floor(log10((double)i))) * 17), 63, 17, 23, hbcmem, 0, ypos, SRCCOPY);
	}

	SelectObject(hbcmem, bitold);
}

// Draws the current area around chip, as well as the chip count, time left and side info
void DrawMap()
{
	InvalidateRect(hWnd, NULL, true);
}