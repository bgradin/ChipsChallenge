/* Native equivalents of the verified packed GDI/USER contracts. */

static u32 ui_graphics(TaskUi *ui, UiArena *arena, u16 ordinal, const u32 *a)
{
    Runtime *r = ui->runtime;
    u32 result = 0;
    if (ordinal == 57)
    {
        u8 *source = ui_buffer(r, a[0], 50, 0);
        u8 *font = ui_alloc(arena, 92);
        if (!source || !font)
        {
            return 0;
        }
        for (u32 i = 0; i < 5; i++)
        {
            ui_put32(font + i * 4, (u32)(int)(short)ui_get16(source + i * 2));
        }
        for (u32 i = 0; i < 8; i++)
        {
            font[20 + i] = source[10 + i];
        }
        for (u32 i = 0; i < 31 && source[18 + i]; i++)
        {
            ui_put16(font + 28 + i * 2, ui_cp1252(source[18 + i]));
        }
        iptr native = UI_CALL(ui, CreateFontIndirectW, (iptr)font);
        result = ui_handle_add(ui, native, 7, 1, 0);
        if (native && !result)
        {
            UI_CALL(ui, DeleteObject, native);
        }
    }
    else if (ordinal == 82)
    {
        int capacity = (short)a[1];
        if (capacity <= 0)
        {
            return 0;
        }
        u8 *dest = ui_buffer(r, a[2], (u32)capacity, 1);
        iptr object = ui_handle(ui, a[0], 7);
        if (!dest || !object)
        {
            return 0;
        }
        iptr type = UI_CALL(ui, GetObjectType, object);
        if (type == 7 && capacity >= 14)
        {
            u8 bitmap[32];
            zero(bitmap, 32);
            if (!UI_CALL(ui, GetObjectW, object, 32, (iptr)bitmap))
            {
                return 0;
            }
            for (u32 i = 0; i < 4; i++)
            {
                int value = (int)ui_get32(bitmap + i * 4);
                if (value < -32768 || value > 32767)
                {
                    r->error = 14;
                    return 0;
                }
            }
            u8 output[14];
            zero(output, 14);
            for (u32 i = 0; i < 4; i++)
            {
                ui_put16(output + i * 2, (u16)ui_get32(bitmap + i * 4));
            }
            output[8] = (u8)ui_get16(bitmap + 16);
            output[9] = (u8)ui_get16(bitmap + 18);
            for (u32 i = 0; i < 14; i++)
            {
                dest[i] = output[i];
            }
            result = 14;
        }
        else if (type == 6 && capacity >= 50)
        {
            u8 font[92];
            zero(font, 92);
            if (!UI_CALL(ui, GetObjectW, object, 92, (iptr)font))
            {
                return 0;
            }
            u8 output[50];
            zero(output, 50);
            for (u32 i = 0; i < 5; i++)
            {
                int v = (int)ui_get32(font + i * 4);
                if (v < -32768 || v > 32767)
                {
                    r->error = 14;
                    return 0;
                }
                ui_put16(output + i * 2, (u16)v);
            }
            for (u32 i = 0; i < 8; i++)
            {
                output[10 + i] = font[20 + i];
            }
            for (u32 i = 0; i < 31 && ui_get16(font + 28 + i * 2); i++)
            {
                output[18 + i] = ui_ansi(r, ui_get16(font + 28 + i * 2));
            }
            if (!r->error)
            {
                for (u32 i = 0; i < 50; i++)
                {
                    dest[i] = output[i];
                }
                result = 50;
            }
        }
    }
    else if (ordinal == 93)
    {
        u8 *dest = ui_buffer(r, a[1], 31, 1);
        iptr dc = ui_handle(ui, a[0], 6);
        if (!dest || !dc)
        {
            return 0;
        }
        u8 metrics[56], output[31];
        zero(metrics, 56);
        if (!UI_CALL(ui, GetTextMetricsA, dc, (iptr)metrics))
        {
            return 0;
        }
        for (u32 i = 0; i < 11; i++)
        {
            int v = (int)ui_get32(metrics + i * 4);
            if (v < -32768 || v > 32767)
            {
                r->error = 14;
                return 0;
            }
            ui_put16(output + (i < 8
                ? i * 2
                : 25 + (i - 8) * 2), (u16)v);
        }
        for (u32 i = 0; i < 3; i++)
        {
            output[16 + i] = metrics[48 + i];
        }
        for (u32 i = 0; i < 4; i++)
        {
            output[19 + i] = metrics[44 + i];
        }
        output[23] = metrics[51];
        output[24] = metrics[52];
        for (u32 i = 0; i < 31; i++)
        {
            dest[i] = output[i];
        }
        result = 1;
    }
    else if (ordinal == 443)
    {
        iptr dc = ui_handle(ui, a[0], 6);
        u8 *header = ui_buffer(r, a[10], 40, 0);
        if (!dc || !header)
        {
            return 0;
        }
        u32 width = ui_get32(header + 4), depth = ui_get16(header + 14),
            compression = ui_get32(header + 16), colors = ui_get32(header + 32);
        if (ui_get32(header) != 40 || !width || width > 0x7fffffff || ui_get16(header + 12) != 1 ||
            (depth != 1 && depth != 4 && depth != 8 && depth != 16 && depth != 24 && depth != 32) ||
            (compression != 0 && compression != 3))
        {
            r->error = 14;
            return 0;
        }
        if (!colors && depth <= 8)
        {
            colors = 1u << depth;
        }
        if (colors > 256)
        {
            r->error = 14;
            return 0;
        }
        unsigned long long stride = (((unsigned long long)width * depth + 31) / 32) * 4,
                           length = stride * (u16)a[8];
        if (length > 65536)
        {
            r->error = 9;
            return 0;
        }
        u8 *info = ui_buffer(r, a[10],
                             40 + (compression == 3
                                 ? 12
                                 : 0) + colors * (a[11] == 1
                                 ? 2
                                 : 4), 0),
           *bits = ui_buffer(r, a[9], (u32)length, 0);
        if (!info || !bits)
        {
            return 0;
        }
        result = (u32)UI_CALL(ui, SetDIBitsToDevice, dc, (short)a[1], (short)a[2], (u16)a[3],
                              (u16)a[4], (short)a[5], (short)a[6], (u16)a[7], (u16)a[8], (iptr)bits,
                              (iptr)info, (u16)a[11]);
    }
    else
    {
        r->error = 14;
    }
    return result;
}

static u32 ui_format(TaskUi *ui, State *s, u32 destination, u32 formatPointer)
{
    Runtime *r = ui->runtime;
    u32 length = 0;
    u8 *format = kernel_string(r, formatPointer, &length);
    if (!format)
    {
        return 0;
    }
    u8 output[1024];
    u32 used = 0, offset = 12;
    for (u32 at = 0; at < length; at++)
    {
        if (format[at] != '%')
        {
            if (used >= 1023)
            {
                r->error = 9;
                return 0;
            }
            output[used++] = format[at];
            continue;
        }
        if (++at >= length)
        {
            r->error = 14;
            return 0;
        }
        if (format[at] == '%')
        {
            if (used >= 1023)
            {
                r->error = 9;
                return 0;
            }
            output[used++] = '%';
            continue;
        }
        int left = 0, padZero = 0, alternate = 0;
        while (at < length && (format[at] == '-' || format[at] == '0' || format[at] == '#'))
        {
            left |= format[at] == '-';
            padZero |= format[at] == '0';
            alternate |= format[at] == '#';
            at++;
        }
        u32 width = 0, precision = 0;
        int hasPrecision = 0;
        while (at < length && format[at] >= '0' && format[at] <= '9')
        {
            width = width * 10 + format[at++] - '0';
            if (width > 1023)
            {
                r->error = 9;
                return 0;
            }
        }
        if (at < length && format[at] == '.')
        {
            at++;
            hasPrecision = 1;
            while (at < length && format[at] >= '0' && format[at] <= '9')
            {
                precision = precision * 10 + format[at++] - '0';
                if (precision > 1023)
                {
                    r->error = 9;
                    return 0;
                }
            }
        }
        int wide = 0, near = 0;
        if (at < length && (format[at] == 'l' || format[at] == 'h'))
        {
            wide = format[at] == 'l';
            near = format[at] == 'h';
            at++;
        }
        if (at >= length)
        {
            r->error = 14;
            return 0;
        }
        u8 conversion = format[at], digits[40];
        const u8 *text = digits;
        u32 count = 0;
        int negative = 0;
        u32 value = __win16_read_word(r, s->ss, (u16)(s->sp + offset));
        offset += 2;
        if (conversion == 's' || conversion == 'S')
        {
            if (!near)
            {
                value |= (u32)__win16_read_word(r, s->ss, (u16)(s->sp + offset)) << 16;
                offset += 2;
            }
            else
            {
                value |= (u32)s->ds << 16;
            }
            static const u8 nullText[] = "(null)";
            if (!value)
            {
                text = nullText;
                count = 6;
            }
            else
            {
                text = kernel_string(r, value, &count);
            }
            if (!text)
            {
                return 0;
            }
            if (hasPrecision && count > precision)
            {
                count = precision;
            }
        }
        else if (conversion == 'c' || conversion == 'C')
        {
            digits[0] = (u8)value;
            count = 1;
        }
        else if (conversion == 'd' || conversion == 'i' || conversion == 'u' || conversion == 'x' ||
                 conversion == 'X')
        {
            if (wide)
            {
                value |= (u32)__win16_read_word(r, s->ss, (u16)(s->sp + offset)) << 16;
                offset += 2;
            }
            int signedValue = wide
                ? (int)value
                : (short)value;
            if ((conversion == 'd' || conversion == 'i') && signedValue < 0)
            {
                negative = 1;
                value = 0u - (u32)signedValue;
            }
            u32 base = conversion == 'x' || conversion == 'X'
                ? 16
                : 10;
            u8 reverse[32];
            u32 n = 0;
            do
            {
                u32 digit = value % base;
                reverse[n++] =
                    (u8)(digit < 10
                        ? '0' + digit
                        : (conversion == 'X'
                                     ? 'A'
                                     : 'a') +
                                 digit - 10);
                value /= base;
            } while (value);
            if (negative)
            {
                digits[count++] = '-';
            }
            if (alternate && base == 16)
            {
                digits[count++] = '0';
                digits[count++] = conversion;
            }
            u32 zeros = hasPrecision && precision > n
                ? precision - n
                : 0;
            if (count + zeros + n >= sizeof(digits))
            {
                if (precision > 32)
                {
                    r->error = 14;
                    return 0;
                }
            }
            while (zeros--)
            {
                digits[count++] = '0';
            }
            while (n)
            {
                digits[count++] = reverse[--n];
            }
        }
        else
        {
            r->error = 14;
            return 0;
        }
        u32 padding = width > count
            ? width - count
            : 0;
        if (used + count + padding > 1023)
        {
            r->error = 9;
            return 0;
        }
        if (!left)
        {
            if (negative && padZero && !hasPrecision)
            {
                output[used++] = '-';
                text++;
                count--;
                negative = 0;
            }
            while (padding)
            {
                output[used++] = padZero && !hasPrecision
                    ? '0'
                    : ' ';
                padding--;
            }
        }
        for (u32 i = 0; i < count; i++)
        {
            output[used++] = text[i];
        }
        if (left)
        {
            while (padding--)
            {
                output[used++] = ' ';
            }
        }
    }
    u8 *dest = ui_buffer(r, destination, used + 1, 1);
    if (!dest)
    {
        return 0;
    }
    for (u32 i = 0; i < used; i++)
    {
        dest[i] = output[i];
    }
    dest[used] = 0;
    return used;
}

static u32 ui_user_structure(TaskUi *ui, UiArena *b, State *s, u16 ordinal, const u32 *a)
{
    Runtime *r = ui->runtime;
    u32 result = 0;
    if (ordinal == 1)
    {
        iptr window = ui_handle(ui, a[0], 5), text = (iptr)ui_wide(b, a[1], 0),
             caption = (iptr)ui_wide(b, a[2], 0);
        if (!r->error)
        {
            result = (u32)UI_CALL(ui, MessageBoxW, window, text, caption, (u16)a[3]);
        }
    }
    else if (ordinal == 5)
    {
        if (a[0] == ui->instance)
        {
            u8 message[48];
            zero(message, 48);
            UI_CALL(ui, PeekMessageW, (iptr)message, 0, 0, 0, 0);
            result = 1;
        }
    }
    else if (ordinal == 6)
    {
        UI_CALL(ui, PostQuitMessage, (short)a[0]);
    }
    else if (ordinal == 15)
    {
        result = (u32)UI_CALL(ui, GetTickCount);
    }
    else if (ordinal == 33)
    {
        if (!ui_buffer(r, a[1], 8, 1))
        {
            return 0;
        }
        iptr window = ui_handle(ui, a[0], 5);
        int rect[4] = {0};
        if (window && !r->error && UI_CALL(ui, GetClientRect, window, (iptr)rect))
        {
            ui_write_rect(r, a[1], rect);
        }
    }
    else if (ordinal == 61)
    {
        iptr window = ui_handle(ui, a[0], 5);
        int *rect = ui_rect(b, a[3]), *clip = ui_rect(b, a[4]);
        if (window && !r->error)
        {
            UI_CALL(ui, ScrollWindow, window, (short)a[1], (short)a[2], (iptr)rect, (iptr)clip);
        }
    }
    else if (ordinal == 78)
    {
        u8 *rect = ui_buffer(r, a[0], 8, 1);
        if (rect)
        {
            for (u32 i = 0; i < 4; i++)
            {
                ui_put16(rect + i * 2, (u16)(ui_get16(rect + i * 2) + (i < 2
                    ? -(short)a[i % 2 + 1]
                    : (short)a[i % 2 + 1])));
            }
        }
    }
    else if (ordinal == 85)
    {
        if (!ui_buffer(r, a[3], 8, 1))
        {
            return 0;
        }
        iptr dc = ui_handle(ui, a[0], 6);
        int *rect = ui_rect(b, a[3]);
        u32 length = 0;
        u8 *text = (short)a[2] < 0
            ? kernel_string(r, a[1], &length)
            : ui_buffer(r, a[1], length = (u16)a[2], 0);
        u16 *wide = text
            ? ui_wide_bytes(b, text, length)
            : 0;
        if (dc && wide && rect && !r->error)
        {
            result = (u32)UI_CALL(ui, DrawTextW, dc, (iptr)wide, length, (iptr)rect, (u16)a[4]);
            ui_write_rect(r, a[3], rect);
        }
    }
    else if (ordinal == 88)
    {
        iptr window = ui_handle(ui, a[0], 5);
        if (window && !r->error)
        {
            result = (u32)UI_CALL(ui, EndDialog, window, (short)a[1]);
        }
    }
    else if (ordinal == 92)
    {
        iptr window = ui_handle(ui, a[0], 5), text = (iptr)ui_wide(b, a[2], 0);
        if (window && !r->error)
        {
            UI_CALL(ui, SetDlgItemTextW, window, (short)a[1], text);
        }
    }
    else if (ordinal == 93)
    {
        u32 cap = (u16)a[3];
        if (!cap)
        {
            return 0;
        }
        u8 *dest = ui_buffer(r, a[2], cap, 1);
        u16 *wide = ui_alloc(b, cap * 2);
        iptr window = ui_handle(ui, a[0], 5);
        if (dest && wide && window && !r->error)
        {
            int n = (int)UI_CALL(ui, GetDlgItemTextW, window, (short)a[1], (iptr)wide, cap);
            if (n < 0 || (u32)n >= cap)
            {
                r->error = 14;
                return 0;
            }
            for (int i = 0; i < n; i++)
            {
                dest[i] = ui_ansi(r, wide[i]);
            }
            dest[n] = 0;
            result = (u32)n;
        }
    }
    else if (ordinal == 95)
    {
        u8 *dest = a[2]
            ? ui_buffer(r, a[2], 2, 1)
            : 0;
        iptr window = ui_handle(ui, a[0], 5);
        int valid = 0;
        u32 value = 0;
        if (window && !r->error)
        {
            value = (u32)UI_CALL(ui, GetDlgItemInt, window, (short)a[1], (iptr)&valid, (u16)a[3]);
        }
        if (a[3] && ((int)value < -32768 || (int)value > 32767) || !a[3] && value > 65535)
        {
            valid = 0;
        }
        if (dest)
        {
            ui_put16(dest, !!valid);
        }
        result = valid
            ? (u16)value
            : 0;
    }
    else if (ordinal == 102)
    {
        if (!ui_buffer(r, a[0], 8, 1))
        {
            return 0;
        }
        int *rect = ui_rect(b, a[0]);
        if (rect && !r->error)
        {
            result = (u32)UI_CALL(ui, AdjustWindowRect, (iptr)rect, a[1], (u16)a[2]);
            if (result)
            {
                ui_write_rect(r, a[0], rect);
            }
        }
    }
    else if (ordinal == 125 || ordinal == 127)
    {
        iptr window = ui_handle(ui, a[0], 5);
        int *rect = ui_rect(b, a[1]);
        if (!r->error)
        {
            if (ordinal == 125)
            {
                UI_CALL(ui, InvalidateRect, window, (iptr)rect, (u16)a[2]);
            }
            else
            {
                UI_CALL(ui, ValidateRect, window, (iptr)rect);
            }
        }
    }
    else if (ordinal == 411)
    {
        iptr menu = ui_handle(ui, a[0], 8), item = (u16)a[2], text = 0;
        if (a[1] & 16)
        {
            item = ui_handle(ui, a[2], 8);
        }
        if (a[1] & 4)
        {
            text = ui_handle(ui, a[3] & 65535, 7);
        }
        else if (a[1] & 0x100)
        {
            text = a[3];
        }
        else
        {
            text = (iptr)ui_wide(b, a[3], 0);
        }
        if (menu && !r->error)
        {
            result = (u32)UI_CALL(ui, AppendMenuW, menu, (u16)a[1], item, text);
        }
    }
    else if (ordinal == 420)
    {
        result = ui_format(ui, s, a[0], a[1]);
    }
    else if (ordinal == 483)
    {
        if (a[0] == 0x16)
        {
            u8 *dest = ui_buffer(r, a[2], 2, 1);
            u32 value = 0;
            if (dest)
            {
                result = (u32)UI_CALL(ui, SystemParametersInfoW, 0x16, (u16)a[1], (iptr)&value,
                                      (u16)a[3]);
                if (result)
                {
                    if (value > 65535)
                    {
                        r->error = 14;
                        return 0;
                    }
                    ui_put16(dest, (u16)value);
                }
            }
        }
        else if (a[0] == 0x17 && !a[2])
        {
            result = (u32)UI_CALL(ui, SystemParametersInfoW, 0x17, (u16)a[1], 0, (u16)a[3]);
        }
        else
        {
            r->error = 14;
        }
    }
    else
    {
        r->error = 14;
    }
    return result;
}

ABI NOINLINE u16 __win16_ui_structure(Runtime *r, State *s, u16 module, u16 ordinal)
{
    if (r->error)
    {
        return 0;
    }
    const UiContract *c = ui_contract(module, ordinal);
    u32 args[12] = {0};
    TaskUi *ui = ui_context(r, s);
    if (!ui || !ui_read_arguments(r, s, c, args))
    {
        return 0;
    }
    UiArena arena = {r, 0};
    u32 result = module == 3
        ? ui_graphics(ui, &arena, ordinal, args)
        : module == 2
                ? ui_user_structure(ui, &arena, s, ordinal, args)
                : 0;
    ui_destroy_arena(&arena);
    if (!r->error && c->result)
    {
        s->ax = (u16)result;
        if (c->result == 4)
        {
            s->dx = (u16)(result >> 16);
        }
    }
    return c->cleanup;
}
