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
bool OpenDatFile(ifstream& in)
{
	in.open(CurrentDirectory() + "\\CHIPS.DAT", ios::in | ios::binary);

	if(in.fail())
		ReportError("Corrupt or inaccesible CHIPS.DAT file.");

	string code = ReadLong(in);
   
    if(!in.fail() && code != "0002AAAC") // Is this a valid map pack?
	{
        ReportError("This is NOT a valid chips data file!");
        return false;
	}
	
	return true;
}

// The basis for read operations:
//   Reads a byte from the input file
unsigned char ReadByte(ifstream& in)
{
	return in.get();
}

// Reads a word, which is two bytes
string ReadWord(ifstream& in)
{
	string str = "  ";
	str[1] = ReadByte(in);
	str[0] = ReadByte(in);
	return ctoh(str);
}

// Reads a long, which is two words
string ReadLong(ifstream& in)
{
	string str = ReadWord(in);
    return ReadWord(in) + str;
}