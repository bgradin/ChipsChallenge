#include "stdafx.h"

void createDirectoryRecursively(const wstring &directory)
{
	static const wstring separators(L"\\/");

	DWORD fileAttributes = ::GetFileAttributesW(directory.c_str());

	if(fileAttributes == INVALID_FILE_ATTRIBUTES)
	{
		// Recursively do it all again for the parent directory, if any
		size_t slashIndex = directory.find_last_of(separators);
		if(slashIndex != wstring::npos)
			createDirectoryRecursively(directory.substr(0, slashIndex));
	}

	// Create the last directory on the path
	BOOL result = ::CreateDirectoryW(directory.c_str(), nullptr);
	if(result == FALSE)
		throw runtime_error("Could not create directory");
	else
	{
		// Specified directory name already exists as a file or directory
		bool isDirectoryOrJunction = ((fileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) || ((fileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0);

		if(!isDirectoryOrJunction)
			throw runtime_error("Could not create directory because a file with the same name exists");
	}
}

INI::INI(const char* fileName)
{
	m_fileName = fileName;

	if (m_fileName == "")
	{
		// Get value of %AppData%
		char* environmentString = new char[MAX_PATH];
		size_t len = MAX_PATH;
		_dupenv_s(&environmentString, &len, "APPDATA");

		m_fileName = string(environmentString) + "\\Chip's Challenge\\entpack.ini";

		delete [] environmentString;
	}

	// Open File
	fstream inFile;
	string temp, appdata;
	char buffer[1000];

	open(inFile);
	
	// Read file
	do
	{
		inFile.getline(buffer,1000);
		temp = buffer;
		if (temp.find('=') != -1)
			m_data.insert(pair<string, string>(temp.substr(0, temp.find('=')), temp.substr(temp.find('=') + 1)));
	} while (inFile.good());

	inFile.close();
}

void INI::write()
{
	// Clear the file
	fstream outputStream;
	open(outputStream, ios_base::out);
	outputStream.close();

	// Reopen file
	outputStream.open(m_fileName);

	// Header
	outputStream << "[Chip's Challenge]";

	// Write terms in map to file
	for (map<string, string>::iterator cur = m_data.begin(); cur != m_data.end(); cur++)
		outputStream << endl << cur->first << "=" << cur->second;

	outputStream.close();
}

void INI::open(fstream& fileStream, ios_base::openmode openMode)
{
	if (fileStream.is_open())
		fileStream.close();

	ifstream inFile;
	inFile.open(m_fileName);

	// If the INI file doesn't already exist, we have to create it
	if(inFile.fail())
	{
		string s = m_fileName.substr(0, string(m_fileName).find_last_of('\\'));
		DWORD ftyp = GetFileAttributesA(s.c_str());
		createDirectoryRecursively(stows(s));

		ofstream outFile(m_fileName);
		outFile << flush << "[Chip's Challenge]" << endl;
		outFile << "Highest Level=1" << endl;
		outFile.close();
	}
	else
		inFile.close();
	
	fileStream.open(m_fileName, openMode);
}