/*
 * <signal.h> wrapper functions.
 *
 * Authors:
 *   Jonathan Pryor (jonpryor@vt.edu)
 *   Jonathan Pryor (jpryor@novell.com)
 *
 * Copyright (C) 2004-2005 Jonathan Pryor
 * Copyright (C) 2008 Novell, Inc.
 */

#include <signal.h>

#include "map.h"
#include "mph.h"

#ifndef PLATFORM_WIN32
#include <sys/time.h>
#include <sys/types.h>
#include <unistd.h>
#include <stdlib.h>
#include <string.h>
#include <pthread.h>
#endif

G_BEGIN_DECLS

typedef void (*mph_sighandler_t)(int);
typedef struct Mono_Unix_UnixSignal_SignalInfo signal_info;

void*
Mono_Posix_Stdlib_SIG_DFL (void)
{
	return SIG_DFL;
}

void*
Mono_Posix_Stdlib_SIG_ERR (void)
{
	return SIG_ERR;
}

void*
Mono_Posix_Stdlib_SIG_IGN (void)
{
	return SIG_IGN;
}

void
Mono_Posix_Stdlib_InvokeSignalHandler (int signum, void *handler)
{
	mph_sighandler_t _h = (mph_sighandler_t) handler;
	_h (signum);
}

#ifndef PLATFORM_WIN32

int
Mono_Posix_Syscall_psignal (int sig, const char* s)
{
	errno = 0;
	psignal (sig, s);
	return errno == 0 ? 0 : -1;
}

#define NUM_SIGNALS 64
static signal_info signals[NUM_SIGNALS];

static void
default_handler (int signum)
{
	int i;
	for (i = 0; i < NUM_SIGNALS; ++i) {
		int fd;
		signal_info* h = &signals [i];
		if (g_atomic_int_get (&h->signum) != signum)
			continue;
		g_atomic_int_inc (&h->count);
		fd = g_atomic_int_get (&h->write_fd);
		if (fd > 0) {
			char c = signum;
			write (fd, &c, 1);
		}
	}
}

static pthread_mutex_t signals_mutex = PTHREAD_MUTEX_INITIALIZER;

void*
Mono_Unix_UnixSignal_install (int sig)
{
	int i, mr;
	signal_info* h = NULL; 
	int have_handler = 0;
	void* handler;

	mr = pthread_mutex_lock (&signals_mutex);
	if (mr != 0) {
		errno = mr;
		return NULL;
	}

	for (i = 0; i < NUM_SIGNALS; ++i) {
		if (h == NULL && signals [i].signum == 0) {
			h = &signals [i];
			h->handler = signal (sig, default_handler);
			if (h->handler == SIG_ERR) {
				h->handler = NULL;
				h = NULL;
				break;
			}
			else {
				h->have_handler = 1;
			}
		}
		if (!have_handler && signals [i].signum == sig &&
				signals [i].handler != default_handler) {
			have_handler = 1;
			handler = signals [i].handler;
		}
		if (h && have_handler)
			break;
	}

	if (h && have_handler) {
		h->have_handler = 1;
		h->handler      = handler;
	}

	if (h) {
		g_atomic_int_set (&h->count, 0);
		g_atomic_int_set (&h->signum, sig);
	}

	pthread_mutex_unlock (&signals_mutex);

	return h;
}

static int
count_handlers (int signum)
{
	int i;
	int count = 0;
	for (i = 0; i < NUM_SIGNALS; ++i) {
		if (signals [i].signum == signum)
			++count;
	}
	return count;
}

int
Mono_Unix_UnixSignal_uninstall (void* info)
{
	signal_info* h;
	int mr, r = -1;

	mr = pthread_mutex_lock (&signals_mutex);
	if (mr != 0) {
		errno = mr;
		return -1;
	}

	h = info;

	if (h == NULL || h < signals || h > &signals [NUM_SIGNALS])
		errno = EINVAL;
	else {
		/* last UnixSignal -- we can unregister */
		if (h->have_handler && count_handlers (h->signum) == 1) {
			mph_sighandler_t p = signal (h->signum, h->handler);
			if (p != SIG_ERR)
				r = 0;
			h->handler      = NULL;
			h->have_handler = 0;
		}
		h->signum = 0;
	}

	pthread_mutex_unlock (&signals_mutex);

	return r;
}

static int
setup_pipes (signal_info** signals, int count, fd_set *read_fds, int *max_fd)
{
	int i, r;
	for (i = 0; i < count; ++i) {
		signal_info* h;
		int filedes[2];

		if ((r = pipe (filedes)) != 0) {
			break;
		}
		h = signals [i];
		h->read_fd  = filedes [0];
		h->write_fd = filedes [1];
		if (h->read_fd > *max_fd)
			*max_fd = h->read_fd;
		FD_SET (h->read_fd, read_fds);
	}
	return r;
}

static void
teardown_pipes (signal_info** signals, int count)
{
	int i;
	for (i = 0; i < count; ++i) {
		signal_info* h = signals [i];
		if (h->read_fd != 0)
			close (h->read_fd);
		if (h->write_fd != 0)
			close (h->write_fd);
		h->read_fd  = 0;
		h->write_fd = 0;
	}
}

static int
wait_for_any (signal_info** signals, int count, int max_fd, fd_set* read_fds, int timeout)
{
	int r, idx;
	do {
		struct timeval tv;
		struct timeval *ptv = NULL;
		if (timeout != -1) {
			tv.tv_sec  = timeout / 1000;
			tv.tv_usec = (timeout % 1000)*1000;
			ptv = &tv;
		}

		r = select (max_fd + 1, read_fds, NULL, NULL, ptv);
	} while (r == -1 && errno == EINTR);

	idx = -1;
	if (r == 0)
		idx = timeout;
	else if (r > 0) {
		int i;
		for (i = 0; i < count; ++i) {
			signal_info* h = signals [i];
			if (FD_ISSET (h->read_fd, read_fds)) {
				char c;
				read (h->read_fd, &c, 1); /* ignore any error */
				if (idx == -1)
					idx = i;
			}
		}
	}

	return idx;
}

/*
 * returns: -1 on error:
 *          timeout on timeout
 *          index into _signals array of signal that was generated on success
 */
int
Mono_Unix_UnixSignal_WaitAny (void** _signals, int count, int timeout /* milliseconds */)
{
	fd_set read_fds;
	int mr, r;
	int max_fd = 0;

	signal_info** signals = (signal_info**) _signals;

	mr = pthread_mutex_lock (&signals_mutex);
	if (mr != 0) {
		errno = mr;
		return -1;
	}

	FD_ZERO (&read_fds);

	r = setup_pipes (signals, count, &read_fds, &max_fd);
	if (r == 0) {
		r = wait_for_any (signals, count, max_fd, &read_fds, timeout);
	}
	teardown_pipes (signals, count);

	pthread_mutex_unlock (&signals_mutex);

	return r;
}

#endif /* ndef PLATFORM_WIN32 */


G_END_DECLS

/*
 * vim: noexpandtab
 */
