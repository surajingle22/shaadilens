import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/** Root component: just a router outlet. Admin and guest areas each bring their own layout. */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  template: '<router-outlet />',
})
export class App {}
