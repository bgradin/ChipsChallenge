#include "stdafx.h"
using namespace std;

class Game;

class Tile
{
	int m_type;

public:
	Tile() : m_type(0) {}
	Tile(int newType) : m_type(newType) {}

	int get() const {return m_type;}

	int operator=(const int rhs){return (m_type = rhs);}
	int operator+=(const int rhs){return (m_type += rhs);}
	int operator-=(const int rhs){return (m_type -= rhs);}
	bool operator==(const int rhs){return bool(m_type == rhs);}
	bool operator!=(const int rhs){return bool(m_type != rhs);}
	bool operator>(const int rhs){return bool(m_type > rhs);}
	bool operator<(const int rhs){return bool(m_type < rhs);}
	bool operator>=(const int rhs){return bool(m_type >= rhs);}
	bool operator<=(const int rhs){return bool(m_type <= rhs);}
};

class MapLayer
{
	Tile m_tiles[32][32];
public:
	MapLayer();

	(Tile*) operator[](int row) {return m_tiles[row];}

	bool isEmpty();
};

class Map
{
public:
	Map(){ levelNumber = 1; }

	vector<MapLayer> layers;

	int levelNumber;
	int totalChips;
	int timeLimit;
	int oldMap;
	bool inGame;
	string levelTitle;
	string hint;
	string password;

	bool Load(Game&, int levelID = 1);
	int TryLoad(Game&, string, int level = 0);
};