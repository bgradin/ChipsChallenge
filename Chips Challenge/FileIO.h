
#include "stdafx.h"
using namespace std;

string CurrentDirectory();
bool open_dat(std::ifstream& in);
unsigned char read_byte(std::ifstream& in);
string read_word(std::ifstream& in);
string read_long(std::ifstream& in);