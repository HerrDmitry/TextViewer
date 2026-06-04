import { Component, inject } from '@angular/core';
import { ShellStateService } from '../shell-state.service';

@Component({
  selector: 'app-status-bar',
  standalone: true,
  templateUrl: './status-bar.component.html',
  styleUrl: './status-bar.component.css'
})
export class StatusBarComponent {
  private readonly state = inject(ShellStateService);
  readonly filePath = this.state.activeFilePath;
  readonly wrapMode = this.state.wrapMode;
  readonly isScanning = this.state.isScanning;
  readonly activeScanProgress = this.state.activeScanProgress;

  onWrapToggle(): void {
    this.state.toggleWrapMode();
  }
}
