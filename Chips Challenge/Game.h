//*****************************************************//
//			   Brian Gradin, 2013-2014				   //
//             Chip's challenge -> Game.h              //
//*****************************************************//
// - Contains the direction, Player, Chip, Monster, Trap, and Game classes
// - Initializations for all member functions are either provided inline or are located in:
//		- Game.cpp
//		- GameLogic.cpp

#include "stdafx.h"
using namespace std;

// Directions are defined numerically with the following convention:
#define UP 0
#define LEFT 1
#define DOWN 2
#define RIGHT 3

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
	int set(int x, int y) // Allows a direction to be set via the x and y change
	{
		if (!((x == 0 && abs(y) < 2) || (y == 0 && abs(x) < 2)))
			return m_direction;

		if (x == 0)
			return m_direction = y + 1;

		return m_direction = x + 2;
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
	int x, y;
	int lastMove;
	bool canMove, notForward, movedRecently;
	direction lastDirection;
	direction currentDirection;

	bool operator==(const Player& p) { return x == p.x && y == p.y; }
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
	Monster() { x = y = type = canMove = notForward = 0; }
	Monster(int newX, int newY, int newType) : type(newType / 4)
	{
		x = newX;
		y = newY;
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
	Chip() { x = y = -1; } // Chip's location must be specified by each level

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
	Trap(int b_x, int b_y, int t_x, int t_y, bool o) : m_buttonX(b_x), m_buttonY(b_y), m_trapX(t_x), m_trapY(t_y), isOpen(o) {}

	int getButtonX() { return m_buttonX; }
	int getButtonY() { return m_buttonY; }
	int getTrapX() { return m_trapX; }
	int getTrapY() { return m_trapY; }
	bool isOpen;

	void openTrap() { isOpen = true; }
	void closeTrap() { isOpen = false; }

private:
	int m_buttonX;
	int m_buttonY;
	int m_trapX;
	int m_trapY;
};

typedef deque<pair<pair<int, int>, direction>>::iterator blockIterator;

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
	bool isSolid(int, int, int, int, bool b = false);

	//
	// FUNCTION: isSolid(int, int, int, int, int)
	//
	// PURPOSE:  Returns whether or not a specified tile appears solid to a monster
	//
	bool isSolid(int, int, int, int, int);

	//
	// FUNCTION: isSlippery(int, int, Chip)
	//
	// PURPOSE:  Returns whether or not the current object is standing on a tile which is "slippery"
	//
	bool isSlippery(int, int, Chip c = Chip());

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
	bool isOpen(int, int);

	//
	// FUNCTION: toggleOpen(int, int, bool)
	//
	// PURPOSE:  Allow specification of whether or not the trap at the location is open
	//
	void toggleOpen(int, int, bool);

	//
	// FUNCTION: moveChip(int, int, bool)
	//
	// PURPOSE:  Handles moving Chip to the proper new location
	//				- Always called from handle_chip()
	//				- Takes x and y change parameters
	//
	void moveChip(int, int);

	//
	// FUNCTION: moveMonster(int, int, deque<Monster>::iterator&
	//
	// PURPOSE:  Handles moving a monster to the proper new location
	//				- Always called from handle_monsters()
	//				- Takes x and y change parameters, and a reference to the current monster
	//
	void moveMonster(int, int, deque<Monster>::iterator&, int&);

	//
	// FUNCTION: moveBlock(int, int, deque<pair<pair<int, int>, direction>>::iterator&)
	//
	// PURPOSE:  Handles moving a block to the proper new location
	//				- Always called from handle_moving_blocks()
	//				- Takes x and y change parameters, and a reference to the current moving block
	//
	void moveBlock(int, int, blockIterator&);

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
	bool commonMovement(int, int, int, int, int&, int&, bool m = false, bool b = false);

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
	bool allowOverride(int&, int&);

	//
	// FUNCTION: handleClonerButton(int, int, int, int)
	//
	// PURPOSE:  Handles cloner button pressing
	//				- Takes the current object position and x and y change as parameters
	//
	bool handleClonerButton(int, int, int, int);

	//
	// FUNCTION: redraw(int, int, int, int, int)
	//
	// PURPOSE:  Redraws the tiles a moving object comes from and moves to
	//				- Takes the tile id, position and x and y change as parameters
	//
	void redraw(int, int, int, int, int);

	//
	// FUNCTION: redrawOldTile(int, int, int)
	//
	// PURPOSE:  Redraws the tile a moving object comes from
	//				- Takes the tile id and position as parameters
	//
	void redrawOldTile(int, int, int);

	//
	// FUNCTION: redrawNewTile(int, int, int)
	//
	// PURPOSE:  Redraws the tile a moving object moves to
	//				- Takes the tile id and new position for the object as parameters
	//
	void redrawNewTile(int, int, int);

	//
	// FUNCTION: bottom_most(int, int)
	//
	// PURPOSE:  Returns the object code of the bottom most tile at the specified location
	//
	int  bottomMostTile(int, int);

	//
	// FUNCTION: bottom_most_index(int, int)
	//
	// PURPOSE:  Returns the zero-based index (where zero is the top) of the layer containing the bottom-most tile
	//
	int  bottomMostIndex(int, int);

	//
	// FUNCTION: revealbluewall(int, int)
	//
	// PURPOSE:  Checks if there is a reveal-able wall the specified location, and if there is, reveal it
	//
	void revealBlueWall(int, int);

	//-------------------------------------//
	//           Member variables          //
	//-------------------------------------//

	//// General game and sound objects ////

	Chip chip;
	deque<Monster> monsters;
	MIDI backgroundMusic;
	map<pair<int, int>, pair<int, int>> cloners;
	map<string, wav> soundEffects;
	Map map;
	list<Trap> traps;
	deque<pair<pair<int, int>, direction>> movingBlocks;
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