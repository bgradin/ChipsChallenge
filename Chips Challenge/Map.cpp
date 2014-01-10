#include "stdafx.h"

//
//  FUNCTION: TryLoad(int, string)
//
//  PURPOSE:  Checks a level number with a password
//
int Map::TryLoad(string pass, int level)
{
	ifstream in;

	if (!OpenDatFile(in))
		return false;
	
	int totalLevels = htoi(ReadWord(in)); // Grab how many levels are in the pack:
   
	if(level > totalLevels) // Are there even n levels in this pack?
	{
		if (level > 144) ReportError("Level number out of range");
		return false;
	}

	int tmplvl = 1;

	while (tmplvl < 149)
	{
		int bimap = htoi(ReadWord(in)); // Grab how many bytes in this level

		int nn = level - 1;
		while (nn > 0  && level != 0)
		{
			nn--; // Tell it we went to the next map
			in.ignore(bimap);
			bimap = htoi(ReadWord(in)); // Grab how many bytes in this level
		}
		in.ignore(8);
		int r = htoi(ReadWord(in));
		in.ignore(r); // Number of bytes in the first layer
		r = htoi(ReadWord(in));
		in.ignore(r); // Number of bytes in the second layer
	
		// Number of bytes in the optional area:
		int biop = htoi(ReadWord(in));
	   string store = "";
		// Loop through all the fields:
		while(biop > 0)
		{
			int field = ReadByte(in); // Field Number
			int bif = ReadByte(in);  // Bytes in field
			biop = biop - 2 - bif; // Remove the bytes read this session
			if(field == 6)
			{
				while(bif-- > 1)
					store.push_back(ReadByte(in) ^ 153); // xor used to decrypt password
				ReadByte(in); // Read terminating 0
			// Unknown or unused field, Skip:
			} else in.ignore(bif);
		}

		if (level != 0)
		{
			in.close();
			return bool(pass == store);
		} else if (pass == store)
		{
			in.close();
			return tmplvl;
		}
		else tmplvl++;
	}
	in.close();
	return false;
}

//
//  FUNCTION: MapLayer()
//
//  PURPOSE:  MapLayer class default constructor
//				- Initializes an empty layer
//
MapLayer::MapLayer()
{
	// Initialize all tiles in the layer to 0
	for (int i = 0; i < 32; i++)
		for (int j = 0; j < 32; j++)
			tiles[i][j] = 0;
}

//
//  FUNCTION: isEmpty(int)
//
//  PURPOSE:  Returns whether or not the layer is empty
//
bool MapLayer::isEmpty()
{
	bool result = false; // Default

	// Iterate through the layer to check emptiness
	for (int i = 0; i < 32; i++)
	{
		for (int j = 0; j < 32; j++)
		{
			if (tiles[i][j] != 0) // 0 = empty tile
			{
				result = false;
				break;			// We don't have to keep checking
			}
		}

		if (!result) break; // We don't have to keep checking
	}

	return result;
}

//
//  FUNCTION: LoadMap(Game&, int)
//
//  PURPOSE:  Sets up the specified map and all corresponding variables in the game object
//
bool Map::Load(Game& g, int levelID)
{
	g.chip.isDead = g.chip.lastMoveWasForced = g.isBeaten = g.chip.notForward = g.isPaused = g.isActive = g.isStarted = g.isLoaded = false;
	int nn, xx = 0, yy = 0; // Init variables
	g.monstersCanMove = g.chip.movedRecently = g.upKeyIsPressed = g.leftKeyIsPressed = g.rightKeyIsPressed = g.downKeyIsPressed = g.chip.isOnHintTile = false;

	g.clickedPoint.x = g.clickedPoint.y = 50;

	if (g.map.levelnumber != levelID)
		g.actualTries = g.totalLevels = 0;

	ifstream in;

	if (!OpenDatFile(in))
		return false;

	g.totalLevels = htoi(ReadWord(in)); // Grab how many levels are in the pack:

	if(levelID > g.totalLevels) // Are there even n levels in this pack?
	{
		ReportError("You can't load level number " + itos(levelID) + " since the pack only contains " + itos(g.totalLevels) + " levels!");
		levelID = g.map.levelnumber;
	}

	int bimap = htoi(ReadWord(in)); // Grab how many bytes in this level
	nn = levelID - 1;
	while (nn > 0)
	{
		nn--; // Tell it we went to the next map
		in.ignore(bimap);
		bimap = htoi(ReadWord(in)); // Grab how many bytes in this level
	}

	g.map.levelnumber = htoi(ReadWord(in)); // Level Number
	g.timeLeft = timelimit = htoi(ReadWord(in)); // Time Limit
	totalchips = g.chipsLeft = htoi(ReadWord(in)); // Chips required
	int mapdetail = htoi(ReadWord(in)); // Map detail, useless, no need to store it
	int bifl = htoi(ReadWord(in)); // Number of bytes in the first layer

	// Reset some key values
	hint = "";
	password = "";
	leveltitle = "";

	// We are ingame now:
	ingame = true;

	// Reset Items
	g.chip.hasFlippers = false;
	g.chip.hasFireBoots = false;
	g.chip.hasSkates = false;
	g.chip.hasSuctionBoots = false;
	g.chip.hasGreenKey = false;
	g.chip.blueKeys = 0;
	g.chip.redKeys = 0;
	g.chip.yellowKeys = 0;

	// Reset the layers:
	layers.clear();
	layers.push_back(MapLayer());
	layers.push_back(MapLayer());
   
	// Clear movement deque:
	while(g.recentKeyPresses.size() > 0) g.recentKeyPresses.pop();

	// Clear the moving blocks list:
	g.movingBlocks.clear();

	// Clear the monster list:
	g.monsters.clear();

	// Clear beartrap list:
	g.traps.clear();

	// Clear cloning list:
	g.cloners.clear();

	// Reset Chip
	g.chip.x = 0;
	g.chip.y = 0;

	// Load the first layer:
	while(bifl>0)
	{
		unsigned int tmp = ReadByte(in);
		bifl--;
		if(tmp == 255) // Run-length encoding
		{
			tmp = ReadByte(in);
			int tmpa = ReadByte(in);
			bifl = bifl - 2;
			for (unsigned int i = 0; i < tmp; i++)
			{
				// Assign the tile to the correct place:
				layers[0][xx][yy] = tmpa;
				// Find Chip:
				if (tmp >= 108 && tmp <= 111)
				{
					g.chip.x = xx;
					g.chip.y = yy;
				}
				// Move to the next square:
				xx++;
				if(xx > 31)
				{
					xx = 0;
					yy++;
				}
			}
		} else {
			// Assign the tile:
			layers[0][xx][yy] = tmp;

			// Find Chip:
			if(tmp>=108 && tmp<=111)
			{
				g.chip.x = xx;
				g.chip.y = yy;
			}
			// Move to the next square:
			xx++;

			if(xx > 31)
			{
				xx = 0;
				yy++;
			}
		}
	}
	// Reset Chip Canmove
	g.chip.canMove = 0;

	// Bytes in the second layer:
	int bisl = htoi(ReadWord(in));

	xx = yy = 0;

	// Load the second layer:
	while(bisl>0)
	{
		int tmp = ReadByte(in);
		bisl--;
		if(tmp == 255)
		{
			tmp = ReadByte(in);
			int tmpa = ReadByte(in);
			bisl = bisl - 2;
			for (int i = 0; i < tmp; i++)
			{
				// Assign the tile to the correct place:
				layers[1][xx][yy] = tmpa;
				// Move to the next square:
				xx++;
				if(xx > 31)
				{
					xx = 0;
					yy++;
				}
			}
		} else {
			// Assign the tile:
			layers[1][xx][yy] = tmp;
			// Move to the next square:
			xx++;
			if(xx > 31)
			{
				xx = 0;
				yy++;
			}
		}
	}

	// Number of bytes in the optional area:
	int biop = htoi(ReadWord(in));

	// Loop through all the fields:
	while(biop > 0)
	{
		int field = ReadByte(in); // Field Number
		int bif = ReadByte(in);  // Bytes in field
		biop = biop - 2 - bif; // Remove the bytes read this session

		// Map title:
		if(field == 3)
			while(bif-- > 0)
				leveltitle.push_back(ReadByte(in));
		// List of bear traps:
		else if(field == 4)
		{
			while(bif > 0)
			{
				bif = bif - 10;
				int b_x = htoi(ReadWord(in));
				int b_y = htoi(ReadWord(in));
				int t_x = htoi(ReadWord(in));
				int t_y = htoi(ReadWord(in));
				bool o = bool(htoi(ReadWord(in)) != 0);
				g.traps.push_back(Trap(b_x, b_y, t_x, t_y, o));
			}
		// List of cloning machines:
		} else if(field == 5)
		{
			while(bif > 0)
			{
				bif = bif - 8;
				int b_x = htoi(ReadWord(in));
				int b_y = htoi(ReadWord(in));
				int c_x = htoi(ReadWord(in));
				int c_y = htoi(ReadWord(in));
				g.cloners.insert(make_pair(make_pair(b_x, b_y), make_pair(c_x, c_y)));
			}
		// Password:
		} else if(field == 6)
		{
			while(bif-- > 1)
				password.push_back(ReadByte(in) ^ 153); // xor used to decrypt password
			ReadByte(in); // Read terminating 0
		// Hint:
		} else if(field == 7)
			while(bif-- > 0)
				hint.push_back(ReadByte(in));
		// Level-specific rules
		else if (field == 9)
		{
			ReportError("This isn't supposed to be here!");
		}
		// Monsters:
		else if(field == 10)
		{
			while(bif > 0)
			{
				bif = bif - 2;
				
				int x = ReadByte(in);
				int y = ReadByte(in);
				
				if(layers[0][x][y] >= 64 && layers[0][x][y] < 100 && layers[1][x][y] != 49)
					g.monsters.push_back(Monster(x, y, layers[0][x][y].get()));
				else if (layers[1][x][y] >= 64 && layers[1][x][y] < 100)
					g.monsters.push_back(Monster(x, y, layers[1][x][y].get()));
			}
		// Unknown or unused field, Skip:
		} else in.ignore(bif);
	}

	// Only restart the music if we change maps:
	if((oldmap != levelnumber || oldmap == 0)  && playMusic)
	{
		g.backgroundMusic.stop();
		g.backgroundMusic.start(g.map.levelnumber);
		oldmap = levelnumber;
	}

	if (levelnumber == 1)
		DisableMenuItem(ID_LEVEL_PREVIOUS);
	else EnableMenuItem(ID_LEVEL_PREVIOUS);
	if (levelnumber == 149)
		DisableMenuItem(ID_LEVEL_NEXT);
	else EnableMenuItem(ID_LEVEL_NEXT);

	if (g.saveData["Level" + itos(levelnumber)] == "")
		g.saveData["Level" + itos(levelnumber)] = password;

	if (g.saveData["Current Level"] == "")
		g.saveData["Current Level"] = itos(levelnumber);
	else if (g.saveData["Current Level"] != itos(levelnumber))
		g.saveData.replace("Current Level", itos(levelnumber));

	if (levelnumber > atoi(g.saveData["Highest Level"].c_str()))
		g.saveData.replace("Highest Level", itos(levelnumber));

	in.close();
	DrawMap();
	DrawLevelNumber(levelnumber);
	string str = "Chip's Challenge - " + leveltitle;
	str.pop_back(); // leveltitle has a null byte
	if (g.oddEvenLabel)
		str += (((g.currentFrame % 2) == 0) ? (string) " (odd)" : (string) " (even)");
	TCHAR* tch = new TCHAR[str.length() + 1];
	mbstowcs_s(NULL, tch, str.length() + 1, str.c_str(), str.length());
	SetWindowText(hWnd, tch);
	delete [] tch;

	if (g.isSlippery(g.chip.x, g.chip.y, g.chip)) g.chip.lastMoveWasForced = g.chip.notForward = true;

	g.isLoaded = true;

	return true;
}