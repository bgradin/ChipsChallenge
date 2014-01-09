#include "stdafx.h"

void Game::loadSounds()
{
	string temp;
	bool changed = false;

	function<void (string, string)> loadSoundEffect = [&](string effectName, string defaultValue)
	{
		string effectFileName = saveData[effectName];

		if (effectFileName != "")
			soundEffects[effectName] = wav(effectFileName.c_str());
		else
		{
			changed = true;
			soundEffects[effectName] = wav((saveData[effectName] = defaultValue).c_str());
		}
	};

	loadSoundEffect("PickUpToolSound", "blip2.wav");
	loadSoundEffect("OpenDoorSound", "door.wav");
	loadSoundEffect("ChipDeathSound", "bummer.wav");
	loadSoundEffect("LevelCompleteSound", "ditty1.wav");
	loadSoundEffect("SocketSound", "chimes.wav");
	loadSoundEffect("BlockedMoveSound", "oof3.wav");
	loadSoundEffect("ThiefSound", "strike.wav");
	loadSoundEffect("SoundOnSound", "chimes.wav");
	loadSoundEffect("PickUpChipSound", "click3.wav");
	loadSoundEffect("SwitchSound", "pop2.wav");
	loadSoundEffect("SplashSound", "water2.wav");
	loadSoundEffect("BombSound", "hit3.wav");
	loadSoundEffect("TeleportSound", "teleport.wav");
	loadSoundEffect("TickSound", "click1.wav");
	
	if (changed)
		saveData.write();
}

//
//  FUNCTION: getPosition()
//
//  PURPOSE:  Returns the x and y coordinates of the item tile in the sprite bitmap
//
void Game::getPosition(int code, int &x, int &y, bool transparency)
{
	// Convert code to hexadecimal
	char tmp = code;
	string prt = "";
	prt += tmp;
	string str = ctoh(prt);

	y = str[1];
	if (str[1] >= 'A' && str[1] <= 'F')
		y -= 7;
	y -= '0';
	y *= 32;
	x = (transparency) ? (str[0] - '0' + 3) * 32 : (str[0] - '0') * 32;
}

int Game::getNumberPosition(int num, bool yellow)
{
	return ((yellow) ? (11 - num) * 23 : 276 + ((11 - num) * 23));
}