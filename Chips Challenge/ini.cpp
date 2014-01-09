#include "stdafx.h"

void createDirectoryRecursively(const std::wstring &directory) {
  static const std::wstring separators(L"\\/");
 
  // If the specified directory name doesn't exist, do our thing
  DWORD fileAttributes = ::GetFileAttributesW(directory.c_str());
  if(fileAttributes == INVALID_FILE_ATTRIBUTES) {
 
    // Recursively do it all again for the parent directory, if any
    std::size_t slashIndex = directory.find_last_of(separators);
    if(slashIndex != std::wstring::npos) {
      createDirectoryRecursively(directory.substr(0, slashIndex));
    }
 
    // Create the last directory on the path (the recursive calls will have taken
    // care of the parent directories by now)
    BOOL result = ::CreateDirectoryW(directory.c_str(), nullptr);
    if(result == FALSE) {
      throw std::runtime_error("Could not create directory");
    }
 
  } else { // Specified directory name already exists as a file or directory
 
    bool isDirectoryOrJunction =
      ((fileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) ||
      ((fileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0);
 
    if(!isDirectoryOrJunction) {
      throw std::runtime_error(
        "Could not create directory because a file with the same name exists"
      );
    }
 
  }
}

INI::INI(const char* filename)
{
	file = filename;

	if (file == "")
	{
		char* environmentString = new char[MAX_PATH];
		size_t len = MAX_PATH;
		_dupenv_s(&environmentString, &len, "APPDATA");
		file = string(environmentString) + "\\Chip's Challenge\\entpack.ini";
		delete [] environmentString;
	}

	// Open File
	fstream inFile;
	string temp, appdata;
	char buffer[1000];

	open(inFile);
	
	// Read file
	do {
		inFile.getline(buffer,1000);
		temp = buffer;
		if (temp.find('=') != -1)
			values.insert(pair<string, string>(temp.substr(0, temp.find('=')), temp.substr(temp.find('=') + 1)));
	} while (inFile.good());

	inFile.close();
}

void INI::write()
{
	// Clear the file
	fstream out;
	open(out, ios_base::out);
	out.close();

	// Reopen file
	out.open(file);
	
	if (out.fail())
		int i = 1;

	// Header
	out << "[Chip's Challenge]";

	// Write terms in map to file
	for (map<string, string>::iterator cur = values.begin(); cur != values.end(); cur++)
		out << endl << cur->first << "=" << cur->second;

	out.close();
}

void INI::open(fstream& fs, ios_base::openmode mode)
{
	if (fs.is_open()) fs.close();

	ifstream inFile;
	inFile.open(file);

	// If the INI file doesn't already exist, we have to create it
	if(inFile.fail())
	{
		string s = file.substr(0, string(file).find_last_of('\\'));
		DWORD ftyp = GetFileAttributesA(s.c_str());
		createDirectoryRecursively(stows(s));

		ofstream outFile(file);
		outFile << flush << "[Chip's Challenge]" << endl;
		outFile << "Highest Level=1" << endl;
		outFile.close();
	} else inFile.close();
	
	fs.open(file, mode);
}