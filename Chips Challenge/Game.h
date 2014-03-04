//*****************************************************//
//			   Brian Gradin, 2013-2014				   //
//             Chip's challenge -> Game.h              //
//*****************************************************//
// - Contains the direction, Player, Chip, Monster, Trap, and Game classes
// - Initializations for all member functions are either provided inline or are located in:
//		- Game.cpp
//		- GameLogic.cpp

#include "stdafx.h"

// Directions are defined numerically with the following convention:
#define UP 0
#define LEFT 1
#define DOWN 2
#define RIGHT 3

struct COMPARABLE_POINT
{
	COMPARABLE_POINT() {}
	COMPARABLE_POINT(POINT point) { x = point.x; y = point.y; }
	bool operator==(POINT& rhs) { return x == rhs.x && y == rhs.y;}
	bool operator!=(POINT& rhs) { return !(*this == rhs); }
	bool operator<(const COMPARABLE_POINT& rhs) const { return x < rhs.x || (x == rhs.x && y < rhs.y); }

	int x, y;
};

struct POINT_CHANGE
{
	int DeltaX, DeltaY;

	POINT_CHANGE() : DeltaX(0), DeltaY(0) {}
	POINT_CHANGE(int x, int y) : DeltaX(x), DeltaY(y) {}

	bool operator==(int rhs) { return DeltaX + DeltaY == rhs; }
	bool operator!=(int rhs) { return !(*this == rhs); }
};

class direction
{
	int m_direction;

public:
	// Constructors
	direction() : m_direction(UP) {}
	direction(const int num) { m_direction = (num < 0 || num > 3) ? 0 : num; }

	// Methods
	direction& get() { return *this; }
	//void get(int& d) {d = dir;}
	int set(POINT_CHANGE change)
	{
		if (!((change.DeltaX == 0 && abs(change.DeltaY) < 2) || (change.DeltaY == 0 && abs(change.DeltaX) < 2)))
			return m_direction;

		if (change.DeltaX == 0)
			return m_direction = change.DeltaY + 1;

		return m_direction = change.DeltaX + 2;
	}
	int set(int deltaX, int deltaY) // Allows a direction to be set via the x and y change
	{
		return set(POINT_CHANGE(deltaX, deltaY));
	}
	int deltaX() { return (m_direction % 2 == 1) ? m_direction - 2 : 0 ; }
	int deltaY() { return (m_direction % 2 == 1) ? 0 : m_direction - 1; }
	int toInt() { return m_direction; }

	// Operator overloads
	bool operator==(const direction& rhs){ return rhs.m_direction == m_direction; }
	bool operator==(const int s) { return s == m_direction; }
	bool operator!=(const direction& rhs){ return !(*this == rhs); }
	bool operator!=(const int s){ return !(*this == direction(s)); }
	direction operator=(const direction& rhs){ m_direction = rhs.m_direction; return *this; }
	direction operator=(const int s)
	{
		if (s < 0 || s > 3) return *this = direction();
		return *this = direction(s);
	}
};

class Player
{
public:
	POINT location;
	int lastMove;
	bool canMove, notForward, movedRecently;
	direction lastDirection;
	direction currentDirection;

	bool operator==(const Player& p) { return location.x == p.location.x && location.y == p.location.y; }
};

// Monster types
#define BLOCK 3
#define BUG 16
#define FIRE_BUG 17
#define PINK_BALL 18
#define TANK 19
#define GLIDER 20
#define TEETH 21
#define WALKER 22
#define BLOB 23
#define PARAMECIUM 24

class Monster : public Player
{
public:
	Monster() { location.x = location.y = type = canMove = notForward = 0; }
	Monster(POINT newLocation, int newType) : type(newType / 4)
	{
		location.x = newLocation.x;
		location.y = newLocation.y;
		canMove = true;
		notForward = false;
		skipFrame = false;
		currentDirection = newType % 4;
		movedRecently = false;
	}

	bool skipFrame; // Only used for teeth and blobs
	int type;
};

class Chip: public Player
{
public:
	Chip() {} // Chip's location must be specified by each level

	bool lastMoveWasForced;
	bool isDead;
	bool isOnHintTile;

	// Items
	bool hasFlippers;
	bool hasFireBoots;
	bool hasSkates;
	bool hasSuctionBoots;
	bool hasGreenKey;
	int blueKeys;
	int redKeys;
	int yellowKeys;
};

class Trap
{
public:
	Trap(COMPARABLE_POINT buttonLocation, COMPARABLE_POINT trapLocation, bool open) : m_buttonLocation(buttonLocation), m_trapLocation(trapLocation), isOpen(open) {}

	COMPARABLE_POINT getButtonLocation() { return m_buttonLocation; }
	COMPARABLE_POINT getTrapLocation() { return m_trapLocation; }
	bool isOpen;

	void openTrap() { isOpen = true; }
	void closeTrap() { isOpen = false; }

private:
	COMPARABLE_POINT m_buttonLocation;
	COMPARABLE_POINT m_trapLocation;
};

typedef deque<pair<POINT, direction>>::iterator blockIterator;

class Game
{
public:
	Game(){ oddEvenLabel = false; saveData = INI(""); }

	//-------------------------------------//
	//               Methods               //
	//-------------------------------------//

	// Unless otherwise stated, methods are initialized in "Game Moving Functions.cpp"

	//
	// FUNCTION: getPosition(int, int&, bool)
	// 
	// PURPOSE:  Returns the x and y coordinates of the location of an object in the sprite bitmap
	//				- Initialized in Game.cpp
	//
	void getPosition(int, int&, int&, bool transparency = false);

	//
	// FUNCTION: getNumberPosition(int, bool)
	//
	// PURPOSE:  Returns the x and y coordinates of the location of a number in the number bitmap
	//				- Initialized in Game.cpp
	//
	int getNumberPosition(int, bool yellow = false);

	//
	// FUNCTION: handleChip(int)
	//
	// PURPOSE:  Controls the main player's movement
	//				- Called from the main file on each frame
	//
	void handleChip();

	//
	// FUNCTION: handleMonsters(int)
	//
	// PURPOSE:  Controls the movement for all monsters
	//				- Called from the main file on each frame
	//
	void handleMonsters();

	//
	// FUNCTION: handleMovingBlocks()
	//
	// PURPOSE:  Controls the movement for all objects in the moving block list
	//				- Called from the main file on each frame
	//
	void handleMovingBlocks();

	//
	// FUNCTION: isSolid(int, int, int, int, bool)
	//
	// PURPOSE:  Returns whether or not a specified tile appears solid to chip or a moving block
	//
	bool isSolid(POINT_CHANGE, POINT, bool b = false);

	//
	// FUNCTION: isSolid(int, int, int, int, int)
	//
	// PURPOSE:  Returns whether or not a specified tile appears solid to a monster
	//
	bool isSolid(POINT_CHANGE, POINT, int);

	//
	// FUNCTION: isSlippery(int, int, Chip)
	//
	// PURPOSE:  Returns whether or not the current object is standing on a tile which is "slippery"
	//
	bool isSlippery(POINT, Chip c = Chip());

	//
	// FUNCTION: eraseBlock(blockIterator&)
	//
	// PURPOSE:  Erases the moving block pointed to by the iterator parameter from the list of moving blocks, without invalidating the iterator
	//
	void eraseBlock(blockIterator&);

	//
	// FUNCTION: incrementTriesAndReloadMap()
	//
	// PURPOSE:  Increments both try counts, 
	//
	void incrementTriesAndReloadMap();

	//
	// FUNCTION: death(int)
	//
	// PURPOSE:  Handles the various death messages and resetting the map
	//
	void death(int);

	//
	// FUNCTION: chipHasHitMonster()
	//
	// PURPOSE:  Returns whether or not chip is currently colliding with a monster
	//
	bool chipHasHitMonster();

	//
	// FUNCTION: isOpen(int, int)
	//
	// PURPOSE:  Returns whether or not the trap at the specified location is open
	//
	bool isOpen(POINT);

	//
	// FUNCTION: toggleOpen(int, int, bool)
	//
	// PURPOSE:  Allow specification of whether or not the trap at the location is open
	//
	void toggleOpen(POINT, bool);

	//
	// FUNCTION: moveChip(int, int, bool)
	//
	// PURPOSE:  Handles moving Chip to the proper new location
	//				- Always called from handle_chip()
	//				- Takes x and y change parameters
	//
	void moveChip(POINT_CHANGE);

	//
	// FUNCTION: moveMonster(int, int, deque<Monster>::iterator&
	//
	// PURPOSE:  Handles moving a monster to the proper new location
	//				- Always called from handle_monsters()
	//				- Takes x and y change parameters, and a reference to the current monster
	//
	void moveMonster(POINT_CHANGE, deque<Monster>::iterator&, int&);

	//
	// FUNCTION: moveBlock(int, int, deque<pair<pair<int, int>, direction>>::iterator&)
	//
	// PURPOSE:  Handles moving a block to the proper new location
	//				- Always called from handle_moving_blocks()
	//				- Takes x and y change parameters, and a reference to the current moving block
	//
	void moveBlock(POINT_CHANGE, blockIterator&);

	//
	// FUNCTION: commonMovement(int, int, int, int, int&, int&, bool, bool)
	//
	// PURPOSE:  Handles most of the common aspects of movement between all object
	//				- Parameters:
	//					- x and y coordinate of object
	//					- x and y change
	//					- A reference to variables to change if the x and y change needs to change
	//					- Whether or not the object is a monster
	//					- Whether or not the object is a block
	//
	bool commonMovement(POINT, POINT_CHANGE, POINT_CHANGE&, bool m = false, bool b = false);

	//
	// FUNCTION: load_sounds()
	//
	// PURPOSE:  Loads all sound effects into the sound effects container
	//				- Initialized in Game.cpp
	//
	void loadSounds();

	//
	// FUNCTION: allowOverride(int&, int&)
	//
	// PURPOSE:  Returns whether or not an override happened
	//				- Always called from handleChip()
	//				- Takes x and y change as parameters
	//
	bool allowOverride(POINT_CHANGE&);

	//
	// FUNCTION: handleClonerButton(int, int, int, int)
	//
	// PURPOSE:  Handles cloner button pressing
	//				- Takes the current object position and x and y change as parameters
	//
	bool handleClonerButton(POINT, POINT_CHANGE);

	//
	// FUNCTION: redraw(int, int, int, int, int)
	//
	// PURPOSE:  Redraws the tiles a moving object comes from and moves to
	//				- Takes the tile id, position and x and y change as parameters
	//
	void redraw(int, POINT, POINT_CHANGE);

	//
	// FUNCTION: redrawOldTile(int, int, int)
	//
	// PURPOSE:  Redraws the tile a moving object comes from
	//				- Takes the tile id and position as parameters
	//
	void redrawOldTile(int, POINT);

	//
	// FUNCTION: redrawNewTile(int, int, int)
	//
	// PURPOSE:  Redraws the tile a moving object moves to
	//				- Takes the tile id and new position for the object as parameters
	//
	void redrawNewTile(int, POINT);

	//
	// FUNCTION: bottom_most(int, int)
	//
	// PURPOSE:  Returns the object code of the bottom most tile at the specified location
	//
	int  bottomMostTile(POINT);

	//
	// FUNCTION: bottom_most_index(int, int)
	//
	// PURPOSE:  Returns the zero-based index (where zero is the top) of the layer containing the bottom-most tile
	//
	int  bottomMostIndex(POINT);

	//
	// FUNCTION: revealbluewall(int, int)
	//
	// PURPOSE:  Checks if there is a reveal-able wall the specified location, and if there is, reveal it
	//
	void revealBlueWall(POINT);

	//-------------------------------------//
	//           Member variables          //
	//-------------------------------------//

	//// General game and sound objects ////

	Chip chip;
	deque<Monster> monsters;
	MIDI backgroundMusic;
	map<COMPARABLE_POINT, COMPARABLE_POINT> cloners;
	map<string, wav> soundEffects;
	Map map;
	list<Trap> traps;
	deque<pair<POINT, direction>> movingBlocks;
	INI saveData;

	//// Keypressing and movement ////

	queue<direction> recentKeyPresses;
	POINT clickedPoint, centerTileLocation;
	bool leftKeyIsPressed, rightKeyIsPressed, upKeyIsPressed, downKeyIsPressed;

	//// Game control ////

	int currentFrame;
	bool isActive;			// Controls main game loop
	bool isBeaten;			// If set, the victory animation will play
	bool isPaused;
	bool isStarted;
	bool isLoaded;
	int	actualTries;		// Number of tries on which the main player spent more than ten seconds
	int totalTries;			// Total number of tries

	//// Miscellaneous ////

	bool monstersCanMove;	// Used to keep monsters from moving on the first frame
	int totalLevels;
	int timeLeft;
	int chipsLeft;
	bool oddEvenLabel;		// When set, the game window displays odd/even step information
};