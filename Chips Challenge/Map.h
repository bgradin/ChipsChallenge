#include "stdafx.h"
using namespace std;

// Forward declaration
class Game;

// Tile class
//  - The basis for all blocks
class Tile
{
	// Member variables
	int type;

public:
	// Constructors
	Tile() : type(0) {}
	Tile(int newType) : type(newType) {}

	// Methods
	int get() const {return type;}

	// Operators
	int operator=(const int rhs){return (type = rhs);}
	int operator+=(const int rhs){return (type += rhs);}
	int operator-=(const int rhs){return (type -= rhs);}
	bool operator==(const int rhs){return bool(type == rhs);}
	bool operator!=(const int rhs){return bool(type != rhs);}
	bool operator>(const int rhs){return bool(type > rhs);}
	bool operator<(const int rhs){return bool(type < rhs);}
	bool operator>=(const int rhs){return bool(type >= rhs);}
	bool operator<=(const int rhs){return bool(type <= rhs);}
};

// MapLayer Class
//  - Contains the actual tiles for a layer in the map
class MapLayer
{
	Tile tiles[32][32];
public:
	MapLayer();

	(Tile*) operator[](int row) {return tiles[row];}

	bool isEmpty();
};

// Map class
//  - Contains map methods and data
class Map
{
public:
	Map(){levelnumber = 1;}

	// Tiles
	vector<MapLayer> layers;

	// Attributes
	int levelnumber;
	int totalchips;
	int timelimit;
	int oldmap;
	bool ingame;
	string leveltitle;
	string hint;
	string password;

	bool Load(Game& g, int levelID = 1);
	int TryLoad(string pass, int level = 0);
};