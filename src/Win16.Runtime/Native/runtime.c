/* Production whole-task payload. Shared helpers have no acceptance entry points. */
#ifndef NE_WHOLE_TASK
#error "Production runtime requires NE_WHOLE_TASK"
#endif
#include "runtime_common.h"
#include "native_image.h"
