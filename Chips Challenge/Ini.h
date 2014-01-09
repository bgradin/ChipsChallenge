#include "stdafx.h"
using namespace std;

class INI
{
	map<string, string> values;
	string file;

	void open(fstream&, ios_base::openmode mode = ios_base::in);

public:
	INI(){}
	INI(const char*);
	string& operator[](const string s) {return values[s];}
	string replace(string term, string val) {values[term] = val; write(); return val;}
	void write();
};