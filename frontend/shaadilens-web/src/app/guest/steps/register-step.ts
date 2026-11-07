import { Component, OnInit, computed, inject, input, output } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { PublicLink } from '../../core/api/guest-api';

export interface RegisterDetails {
  name: string;
  phone: string;
  email: string;
  consent: boolean;
}

/** The fields the couple asked for on this link, plus the selfie consent checkbox. */
@Component({
  selector: 'app-register-step',
  imports: [ReactiveFormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule],
  template: `
    <main class="step">
      <h1>{{ link().selfieRequired ? 'First, a few details' : 'Before you start' }}</h1>
      <form [formGroup]="form" (ngSubmit)="submit()">
        @if (ask('Name')) {
          <mat-form-field appearance="outline">
            <mat-label>Your name</mat-label>
            <input matInput formControlName="name" autocomplete="name" />
          </mat-form-field>
        }
        @if (ask('Phone')) {
          <mat-form-field appearance="outline">
            <mat-label>Mobile number</mat-label>
            <input matInput formControlName="phone" type="tel" inputmode="tel" autocomplete="tel" placeholder="+91 98765 43210" />
            <mat-error>Please enter a valid mobile number</mat-error>
          </mat-form-field>
        }
        @if (ask('Email')) {
          <mat-form-field appearance="outline">
            <mat-label>Email</mat-label>
            <input matInput formControlName="email" type="email" autocomplete="email" />
            <mat-error>Please enter a valid email</mat-error>
          </mat-form-field>
        }

        @if (link().selfieRequired) {
          <mat-checkbox formControlName="consent" class="consent">
            I agree that my selfie is used <strong>only to find my photos at this wedding</strong>.
            I can delete it anytime from the gallery.
          </mat-checkbox>
        }

        <button mat-flat-button type="submit" class="cta" [disabled]="form.invalid">
          @if (link().selfieRequired) { <mat-icon>photo_camera</mat-icon> }
          {{ link().selfieRequired ? 'Next: take a selfie' : 'Continue' }}
        </button>
      </form>
    </main>
  `,
  styles: `
    .step { min-height: 100dvh; max-width: 440px; margin: 0 auto; padding: 32px 20px; display: flex; flex-direction: column; justify-content: center; }
    h1 { font: var(--mat-sys-headline-small); margin: 0 0 20px; text-align: center; }
    form { display: flex; flex-direction: column; gap: 4px; }
    .consent { margin: 4px 0 16px; }
    .cta { height: 52px; border-radius: 26px; font-size: 16px; }
  `,
})
export class RegisterStep implements OnInit {
  readonly link = input.required<PublicLink>();
  readonly initial = input<RegisterDetails | null>(null);
  readonly submitted = output<RegisterDetails>();

  private readonly fields = computed(() => (this.link().requireRegistration ? this.link().registrationFields : ''));

  protected readonly form = inject(FormBuilder).nonNullable.group({
    name: [''],
    phone: [''],
    email: [''],
    consent: [false],
  });

  ngOnInit(): void {
    const c = this.form.controls;
    if (this.ask('Name')) c.name.addValidators([Validators.required, Validators.maxLength(100)]);
    if (this.ask('Phone')) c.phone.addValidators([Validators.required, Validators.pattern(/^\+?[\d\s-]{10,18}$/)]);
    if (this.ask('Email')) c.email.addValidators([Validators.required, Validators.email]);
    if (this.link().selfieRequired) c.consent.addValidators(Validators.requiredTrue);
    if (this.initial()) this.form.patchValue(this.initial()!);
    this.form.updateValueAndValidity();
  }

  protected ask(field: 'Name' | 'Phone' | 'Email'): boolean {
    return this.fields().includes(field);
  }

  protected submit(): void {
    if (this.form.valid) this.submitted.emit(this.form.getRawValue());
  }
}
