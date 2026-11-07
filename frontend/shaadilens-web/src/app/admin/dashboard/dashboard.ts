import { Component } from '@angular/core';
import { ApiStatus } from '../../shared/api-status/api-status';

@Component({
  selector: 'app-dashboard',
  imports: [ApiStatus],
  template: `
    <header class="page-header"><h1>System status</h1></header>
    <app-api-status />
  `,
})
export class Dashboard {}
