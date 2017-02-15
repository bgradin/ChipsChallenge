#pragma once

#include "IGameWindow.h"

namespace ChipsChallenge
{
	class MainWindow : public IGameWindow
	{
	public:
		MainWindow();

		virtual void Create() {}
		virtual void Show() {}
	};
}