#pragma once

#include "IGameWindow.h"

namespace ChipsChallenge
{
	class Game
	{
	public:
		Game(IGameWindow);

	private:
		IGameWindow m_gameWindow;
	};
}