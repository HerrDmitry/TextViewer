import { Component, inject } from '@angular/core';
import { ShellStateService } from '../shell-state.service';

@Component({
  selector: 'app-text-view-area',
  standalone: true,
  templateUrl: './text-view-area.component.html',
  styleUrl: './text-view-area.component.css'
})
export class TextViewAreaComponent {
  private readonly state = inject(ShellStateService);
  readonly activeTab = this.state.activeTab;
  readonly hasOpenTabs = this.state.hasOpenTabs;
}
