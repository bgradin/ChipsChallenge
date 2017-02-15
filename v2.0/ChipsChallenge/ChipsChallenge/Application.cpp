// Lifetime
// - Initializes members

// Inputs
// (none)

// Members
// - MainWindow (?)
// - Keyboard (?)
// - GraphicsContext (?)
// - Game
// - Audio (?)

// Methods
// pa CreateMainWindow
// pa CreateKeyboard
// pa CreateGraphicsContext (?)
// pa CreateAudio (?)

#include "Application.h"

namespace ChipsChallenge
{
	void Application::Initialize()
	{
		m_mainWindow = CreateMainWindow();
		m_mainWindow->Create();
		m_mainWindow->Show();
	}
}
