import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ProblemDetails } from './api/api.models';

/** Turns any error (usually an API ProblemDetails response) into one readable sentence. */
export function errorMessage(err: unknown, fallback = 'Something went wrong. Please try again.'): string {
  if (err instanceof HttpErrorResponse) {
    if (err.status === 0) return 'Can’t reach the server. Check your connection.';
    if (err.status === 429) return (err.error as ProblemDetails)?.detail ?? 'Too many attempts. Please wait a bit.';
    const p = err.error as ProblemDetails | null;
    if (p?.errors) return Object.values(p.errors).flat()[0] ?? fallback;
    if (p?.detail) return p.detail;
    if (p?.title) return p.title;
  }
  return fallback;
}

/** Small toast messages at the bottom of the screen. */
@Injectable({ providedIn: 'root' })
export class Notify {
  private readonly snack = inject(MatSnackBar);

  ok(message: string): void {
    this.snack.open(message, undefined, { duration: 2500 });
  }

  error(err: unknown, fallback?: string): void {
    this.snack.open(errorMessage(err, fallback), 'OK', { duration: 6000 });
  }
}
