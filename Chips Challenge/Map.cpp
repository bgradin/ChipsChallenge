#include "stdafx.h"

//
//  FUNCTION: TryLoad(int, string)
//
//  PURPOSE:  Checks a level number with a password
//
int Map::TryLoad(Game& game, string password, int level)
{
	ifstream inputStream;

	if (!OpenDatFile(inputStream))
		return -1;
	
	int totalLevels = htoi(ReadWord(inputStream)); // Grab how many levels are in the pack:

	if(level > totalLevels) // Are there even n levels in this pack?
	{
		if (level > game.totalLevels)
			ReportError("Level number out of range");
		return -1;
	}

	int levelIterator = 1;

	while (levelIterator < game.totalLevels)
	{
		// Grab how many bytes in this level
		int levelBytes = htoi(ReadWord(inputStream));

		// Skip levels until we get to the level before the requested level
		int levelsToSkip = levelIterator - 1;
		while (levelsToSkip > 0  && level != 0)
		{
			levelsToSkip--;
			inputStream.ignore(levelBytes);
			levelBytes = htoi(ReadWord(inputStream)); // Grab how many bytes in this level
		}
		inputStream.ignore(8);
		int layerBytes = htoi(ReadWord(inputStream));
		inputStream.ignore(layerBytes);
		layerBytes = htoi(ReadWord(inputStream));
		inputStream.ignore(layerBytes);
	
		// Number of bytes in the optional area:
		int optionalAreaBytes = htoi(ReadWord(inputStream));
		string store = "";

		// Loop through all the fields:
		while(optionalAreaBytes > 0)
		{
			int field = ReadByte(inputStream);
			int fieldBytes = ReadByte(inputStream);
			optionalAreaBytes = optionalAreaBytes - 2 - fieldBytes; // Remove the bytes read this session
			if(field == 6) // 6 = password field
			{
				while(fieldBytes-- > 1)
					store.push_back(ReadByte(inputStream) ^ 153); // xor used to decrypt password
				ReadByte(inputStream); // Read terminating 0
			}
				else inputStream.ignore(fieldBytes);
		}

		if (level != 0)
		{
			inputStream.close();
			return (password == store) ? level : -1;
		}
		else if (password == store)
		{
			inputStream.close();
			return levelIterator;
		}
		else levelIterator++;
	}

	inputStream.close();
	return -1;
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
	{
		for (int j = 0; j < 32; j++)
			m_tiles[i][j] = EMPTY_TILE;
	}
}

//
//  FUNCTION: isEmpty(int)
//
//  PURPOSE:  Returns whether or not the layer is empty
//
bool MapLayer::isEmpty()
{
	// Iterate through the layer to check emptiness
	for (int i = 0; i < 32; i++)
	{
		for (int j = 0; j < 32; j++)
		{
			if (m_tiles[i][j] != EMPTY_TILE)
				return false;
		}
	}

	return true;
}

//
//  FUNCTION: LoadMap(Game&, int)
//
//  PURPOSE:  Sets up the specified map and all corresponding variables in the game object
//
bool Map::Load(Game& game, int levelID)
{
	game.chip.isDead = game.chip.lastMoveWasForced = game.isBeaten = game.chip.notForward = game.isPaused = game.isActive = game.isStarted = game.isLoaded = game.monstersCanMove = game.chip.movedRecently = game.upKeyIsPressed = game.leftKeyIsPressed = game.rightKeyIsPressed = game.downKeyIsPressed = game.chip.isOnHintTile = false;
	int previousLevelID, currentX = 0, currentY = 0;

	game.clickedPoint.x = game.clickedPoint.y = 50;

	if (game.map.levelNumber != levelID)
		game.actualTries = game.totalLevels = 0;

	ifstream inputStream;

	if (!OpenDatFile(inputStream))
		return false;

	game.totalLevels = htoi(ReadWord(inputStream)); // Grab how many levels are in the pack:

	if(levelID > game.totalLevels) // Are there even n levels in this pack?
	{
		ReportError("You can't load level number " + itos(levelID) + " since the pack only contains " + itos(game.totalLevels) + " levels!");
		levelID = game.map.levelNumber;
	}
	
	int levelBytes = htoi(ReadWord(inputStream)); // Grab how many bytes in this level
	previousLevelID = levelID - 1;
	while (previousLevelID > 0)
	{
		previousLevelID--; // Tell it we went to the next map
		inputStream.ignore(levelBytes);
		levelBytes = htoi(ReadWord(inputStream)); // Grab how many bytes in this level
	}

	game.map.levelNumber = htoi(ReadWord(inputStream)); // Level Number
	game.timeLeft = timeLimit = htoi(ReadWord(inputStream)); // Time Limit
	totalChips = game.chipsLeft = htoi(ReadWord(inputStream)); // Chips required
	htoi(ReadWord(inputStream)); // Map detail, useless, no need to store it
	int firstLayerBytes = htoi(ReadWord(inputStream)); // Number of bytes in the first layer

	// Reset some key values
	hint = "";
	password = "";
	levelTitle = "";

	// We are ingame now:
	inGame = true;

	// Reset Items
	game.chip.hasFlippers = false;
	game.chip.hasFireBoots = false;
	game.chip.hasSkates = false;
	game.chip.hasSuctionBoots = false;
	game.chip.hasGreenKey = false;
	game.chip.blueKeys = 0;
	game.chip.redKeys = 0;
	game.chip.yellowKeys = 0;

	// Reset the layers:
	layers.clear();
	layers.reserve(MAX_LAYERS);
	layers.push_back(MapLayer());
	layers.push_back(MapLayer());

	// Clear movement deque:
	while(game.recentKeyPresses.size() > 0)
		game.recentKeyPresses.pop();

	game.movingBlocks.clear();
	game.monsters.clear();
	game.traps.clear();
	game.cloners.clear();

	// Reset Chip
	game.chip.location.x = 0;
	game.chip.location.y = 0;

	// Load the first layer:
	while (firstLayerBytes > 0)
	{
		unsigned int tileID = ReadByte(inputStream);
		firstLayerBytes--;
		if(tileID == 255) // Run-length encoding
		{
			tileID = ReadByte(inputStream);
			int currentTileID = ReadByte(inputStream);
			firstLayerBytes = firstLayerBytes - 2;
			for (unsigned int i = 0; i < tileID; i++)
			{
				layers[0][currentX][currentY] = currentTileID;

				if (tileID >= CHIP_NORTH_TILE && tileID <= CHIP_EAST_TILE)
				{
					game.chip.location.x = currentX;
					game.chip.location.y = currentY;
				}

				currentX++;
				if (currentX > 31)
				{
					currentX = 0;
					currentY++;
				}
			}
		}
		else
		{
			layers[0][currentX][currentY] = tileID;

			if (tileID >= CHIP_NORTH_TILE && tileID <= CHIP_EAST_TILE)
			{
				game.chip.location.x = currentX;
				game.chip.location.y = currentY;
			}

			currentX++;
			if (currentX > 31)
			{
				currentX = 0;
				currentY++;
			}
		}
	}

	game.chip.canMove = 0;

	int secondLayerBytes = htoi(ReadWord(inputStream));

	currentX = currentY = 0;

	// Load the second layer:
	while (secondLayerBytes > 0)
	{
		int tileID = ReadByte(inputStream);
		secondLayerBytes--;
		if (tileID == 255)
		{
			tileID = ReadByte(inputStream);
			int currentTileID = ReadByte(inputStream);
			secondLayerBytes = secondLayerBytes - 2;
			for (int i = 0; i < tileID; i++)
			{
				layers[1][currentX][currentY] = currentTileID;

				currentX++;
				if (currentX > 31)
				{
					currentX = 0;
					currentY++;
				}
			}
		}
		else
		{
			layers[1][currentX][currentY] = tileID;

			currentX++;
			if(currentX > 31)
			{
				currentX = 0;
				currentY++;
			}
		}
	}

	int optionalAreaBytes = htoi(ReadWord(inputStream));

	// Loop through all the fields:
	while(optionalAreaBytes > 0)
	{
		int field = ReadByte(inputStream); 
		int fieldBytes = ReadByte(inputStream);
		optionalAreaBytes = optionalAreaBytes - 2 - fieldBytes; // Remove the bytes read this session

		// Map title:
		if (field == 3)
		{
			while(fieldBytes-- > 0)
				levelTitle.push_back(ReadByte(inputStream));
		}

		// List of bear traps:
		else if (field == 4)
		{
			while (fieldBytes > 0)
			{
				fieldBytes = fieldBytes - 10;
				int buttonX = htoi(ReadWord(inputStream));
				int buttonY = htoi(ReadWord(inputStream));
				int trapX = htoi(ReadWord(inputStream));
				int trapY = htoi(ReadWord(inputStream));
				bool isOpen = bool(htoi(ReadWord(inputStream)) != 0);
				game.traps.push_back(Trap(COMPARABLE_POINT(NewPoint(buttonX, buttonY)), COMPARABLE_POINT(NewPoint(trapX, trapY)), isOpen));
			}
		}

		// List of cloning machines:
		else if(field == 5)
		{
			while (fieldBytes > 0)
			{
				fieldBytes = fieldBytes - 8;
				int buttonX = htoi(ReadWord(inputStream));
				int buttonY = htoi(ReadWord(inputStream));
				int clonerX = htoi(ReadWord(inputStream));
				int clonerY = htoi(ReadWord(inputStream));
				game.cloners[NewPoint(buttonX, buttonY)] = NewPoint(clonerX, clonerY);
			}
		}

		// Password:
		else if (field == 6)
		{
			while (fieldBytes-- > 1)
				password.push_back(ReadByte(inputStream) ^ 153); // xor used to decrypt password
			ReadByte(inputStream); // Read terminating 0
		}

		// Hint:
		else if(field == 7)
		{
			while (fieldBytes-- > 0)
				hint.push_back(ReadByte(inputStream));
		}

		// Level-specific rules
		else if (field == 9)
		{
			ReportError("This isn't supposed to be here!");
		}

		// Monsters:
		else if (field == 10)
		{
			while(fieldBytes > 0)
			{
				fieldBytes = fieldBytes - 2;
				
				int x = ReadByte(inputStream);
				int y = ReadByte(inputStream);
				POINT location = NewPoint(x, y);
				
				if(layers[0][x][y] >= BUG_NORTH_TILE && layers[0][x][y] <= PARAMECIUM_EAST_TILE && layers[1][x][y] != CLONING_MACHINE_TILE)
					game.monsters.push_back(Monster(location, layers[0][x][y].get()));
				else if (layers[1][x][y] >= 64 && layers[1][x][y] < 100)
					game.monsters.push_back(Monster(location, layers[1][x][y].get()));
			}
		}
		
		// Unknown or unused field, Skip:
		else
			inputStream.ignore(fieldBytes);
	}

	// Only restart the music if we change maps:
	if ((oldMap != levelNumber || oldMap == 0)  && playMusic)
	{
		game.backgroundMusic.stop();
		game.backgroundMusic.start(game.map.levelNumber);
		oldMap = levelNumber;
	}

	if (levelNumber == 1)
		DisableMenuItem(hWnd, ID_LEVEL_PREVIOUS);
	else EnableMenuItem(hWnd, ID_LEVEL_PREVIOUS);

	if (levelNumber == game.totalLevels)
		DisableMenuItem(hWnd, ID_LEVEL_NEXT);
	else EnableMenuItem(hWnd, ID_LEVEL_NEXT);

	if (game.saveData["Level" + itos(levelNumber)] == "")
		game.saveData["Level" + itos(levelNumber)] = password;

	if (game.saveData["Current Level"] == "")
		game.saveData["Current Level"] = itos(levelNumber);
	else if (game.saveData["Current Level"] != itos(levelNumber))
		game.saveData.replace("Current Level", itos(levelNumber));

	if (levelNumber > atoi(game.saveData["Highest Level"].c_str()))
		game.saveData.replace("Highest Level", itos(levelNumber));

	inputStream.close();

	DrawMap();
	DrawLevelNumber(levelNumber);

	string str = "Chip's Challenge - " + levelTitle;
	str.pop_back(); // levelTitle has a null byte

	if (game.oddEvenLabel)
		str += (((game.currentFrame % 2) == 0) ? (string) " (odd)" : (string) " (even)");
	TCHAR* tch = new TCHAR[str.length() + 1];
	mbstowcs_s(NULL, tch, str.length() + 1, str.c_str(), str.length());
	SetWindowText(hWnd, tch);

	delete [] tch;

	if (game.isSlippery(NewPoint(game.chip.location.x, game.chip.location.y), game.chip))
		game.chip.lastMoveWasForced = game.chip.notForward = true;

	game.isLoaded = true;

	return true;
}