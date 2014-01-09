#include "stdafx.h"
using namespace std;

#define HEX( x ) setw(2) << setfill('0') << hex << (int)( x )

wstring stows(string str)
{
    return wstring(str.begin(), str.end());
}

string dtoh(int num)
{
	stringstream ss;
	ss << hex << num;
	return ss.str();
}

string strToUpper(string str)
{
	for(unsigned int l = 0; l < str.length(); l++)
		str[l] = toupper(str[l]);
	return str;
}

string ctoh(string str) // 174764
{
	stringstream ss;
	string result = "", part;
	for (unsigned int i = 0; i < str.length(); i++)
	{
		ss.clear();
		unsigned char c = str[i];
		ss << HEX(c);
		ss >> part;
		result.append(part);
	}
	return strToUpper(result);
}

string itos(int num)
{
	stringstream ss;
	string str;
	ss << num;
	ss >> str;
	return str;
}

int htoi(string str)
{
	int result = 0;
	for (unsigned int i = 0; i < str.length(); i++)
	{
		unsigned char c = str[i];
		if (c >= 65 && c <= 70)
			c -= 7;
		int b = c;
		result += (b - 48) * (int)pow(16.0, (int)(str.length() - i - 1));
	}
	return result;
}

int htoi(unsigned char str)
{
	if (str >= 65 && str <= 70)
		str -= 7;
	int b = str;
	return (b - 48);
}