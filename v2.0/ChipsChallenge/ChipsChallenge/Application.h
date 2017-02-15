#pragma once

#include <memory>
#include "Game.h"
#include "Keyboard.h"
#include "MainWindow.h"

namespace ChipsChallenge
{
	class Application
	{
	protected:
		void Initialize();
		virtual std::unique_ptr<MainWindow> CreateMainWindow() = 0;
		virtual Keyboard CreateKeyboard() = 0;

	private:
		// Game m_game;
		std::unique_ptr<MainWindow> m_mainWindow;
	};
}