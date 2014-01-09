#include "stdafx.h"

// Returns the current directory, as a string
string CurrentDirectory()
{
	TCHAR buffer[MAX_PATH];

	GetCurrentDirectory(MAX_PATH, buffer);

	char temp[MAX_PATH];
	wcstombs_s(NULL, temp, MAX_PATH, buffer, MAX_PATH);
	return temp;
}

// Opens CHIPS.DAT for reading
bool open_dat(ifstream& in)
{
	in.open(CurrentDirectory() + "\\CHIPS.DAT", ios::in | ios::binary);

	if(in.fail())
		ReportError("Corrupt or inaccesible CHIPS.DAT file.");

	string code = read_long(in);
   
    if(!in.fail() && code != "0002AAAC") // Is this a valid map pack?
	{
        ReportError("This is NOT a valid chips data file!");
        return false;
	}
	
	return true;
}

// The basis for read operations:
//   Reads a byte from the input file
unsigned char read_byte(ifstream& in)
{
	return in.get();
}

// Reads a word, which is two bytes
string read_word(ifstream& in)
{
	string str = "  ";
	str[1] = read_byte(in);
	str[0] = read_byte(in);
	return ctoh(str);
}

// Reads a long, which is two words
string read_long(ifstream& in)
{
	string str = read_word(in);
    return read_word(in) + str;
}