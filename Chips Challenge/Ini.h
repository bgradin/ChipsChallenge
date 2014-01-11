#include "stdafx.h"
using namespace std;

class INI
{
	map<string, string> m_data;
	string m_fileName;

	void open(fstream&, ios_base::openmode mode = ios_base::in);

public:
	INI(){}
	INI(const char*);
	string& operator[](const string s) { return m_data[s]; }
	string replace(string term, string val) { m_data[term] = val; write(); return val; }
	void write();
};